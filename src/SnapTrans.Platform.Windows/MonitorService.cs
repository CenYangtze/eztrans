using System.ComponentModel;
using System.Runtime.InteropServices;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Geometry;
using SnapTrans.Platform.Windows.Interop;

namespace SnapTrans.Platform.Windows;

public sealed class MonitorService : IMonitorService
{
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        Exception? failure = null;
        bool Enumerate(nint handle, nint dc, ref NativeMethods.Rect unused, nint data)
        {
            try
            {
                var info = new NativeMethods.MonitorInfoEx { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfoEx>(), Device = "" };
                if (!NativeMethods.GetMonitorInfo(handle, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var hr = NativeMethods.GetDpiForMonitor(handle, 0, out var dpi, out _);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                monitors.Add(new(monitors.Count, info.Device, ToPixels(info.Monitor), ToPixels(info.Work), dpi, (info.Flags & 1) != 0));
                return true;
            }
            catch (Exception exception) { failure = exception; return false; }
        }
        if (!NativeMethods.EnumDisplayMonitors(0, 0, Enumerate, 0))
            throw failure ?? new Win32Exception(Marshal.GetLastWin32Error(), "无法枚举显示器。");
        if (monitors.Count == 0) throw new InvalidOperationException("没有可用显示器。");
        return monitors;
    }

    private static PixelRect ToPixels(NativeMethods.Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
