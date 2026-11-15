using System.ComponentModel;
using System.Runtime.InteropServices;
using SnapTrans.Core.Geometry;
using SnapTrans.Platform.Windows.Interop;

namespace SnapTrans.Platform.Windows;

public sealed record DetectedWindow(nint Handle, PixelRect Bounds);
public sealed class WindowSnapshot(IReadOnlyList<DetectedWindow> windows)
{
    public IReadOnlyList<DetectedWindow> Windows { get; } = windows;
    public PixelRect? Find(PixelPoint virtualPoint) => Windows.FirstOrDefault(window => window.Bounds.Contains(virtualPoint))?.Bounds;
}

/// <summary>Snapshot visible top-level windows before overlays appear; no polling, hooks, titles or process text.</summary>
public sealed class WindowDetectionService
{
    public WindowSnapshot Snapshot() => CaptureSnapshot();
    public WindowSnapshot CaptureSnapshot()
    {
        var accepted = new Dictionary<nint, DetectedWindow>();
        uint ownProcess = (uint)Environment.ProcessId;
        nint shell = NativeMethods.GetShellWindow();
        bool Include(nint handle, nint state)
        {
            if (handle == shell || !NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle)) return true;
            _ = NativeMethods.GetWindowThreadProcessId(handle, out var process);
            if (process == ownProcess) return true;
            if (NativeMethods.DwmGetWindowIntAttribute(handle, 14, out int cloaked, sizeof(int)) >= 0 && cloaked != 0) return true;
            var result = NativeMethods.DwmGetWindowRectAttribute(handle, 9, out var rect, (uint)Marshal.SizeOf<NativeMethods.Rect>());
            if (result < 0 && !NativeMethods.GetWindowRect(handle, out rect)) return true;
            var bounds = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (!bounds.IsEmpty) accepted[handle] = new(handle, bounds);
            return true;
        }
        if (!NativeMethods.EnumWindows(Include, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法识别窗口边界。");
        // GetWindow explicitly preserves z-order instead of depending on EnumWindows' undocumented ordering.
        var ordered = new List<DetectedWindow>();
        var visited = new HashSet<nint>();
        for (var handle = NativeMethods.GetTopWindow(0); handle != 0 && visited.Add(handle); handle = NativeMethods.GetWindow(handle, 2))
            if (accepted.Remove(handle, out var window)) ordered.Add(window);
        // A window created/removed during the snapshot may vanish from the z-chain; do not guess
        // its order and let it mask a still-valid window below the pointer.
        return new(ordered);
    }
}
