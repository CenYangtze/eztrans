using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SnapTrans.Core.Geometry;
using SnapTrans.Platform.Windows.Interop;

namespace SnapTrans.Platform.Windows;

public static class WindowPlacement
{
    public static void SetBounds(Window window, PixelRect bounds)
    {
        if (bounds.IsEmpty) throw new ArgumentException("窗口像素尺寸必须大于零。", nameof(bounds));
        window.Dispatcher.VerifyAccess();
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!NativeMethods.SetWindowPos(handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0004 | 0x0010))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法定位窗口。");
    }

    /// <summary>Moves the window without changing its size; coordinates are virtual-screen pixels.</summary>
    public static void SetPosition(Window window, PixelPoint position)
    {
        window.Dispatcher.VerifyAccess();
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!NativeMethods.SetWindowPos(handle, 0, position.X, position.Y, 0, 0, 0x0001 | 0x0004 | 0x0010))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法定位窗口。");
    }

    public static PixelRect GetBounds(Window window)
    {
        window.Dispatcher.VerifyAccess();
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!NativeMethods.GetWindowRect(handle, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static void EnableRoundedCorners(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        int rounded = 2;
        // Unsupported on Windows 10: an unsuccessful HRESULT intentionally keeps square corners.
        _ = NativeMethods.DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
    }

    public static void SetCaptureExcluded(Window window, bool excluded)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!NativeMethods.SetWindowDisplayAffinity(handle, excluded ? 0x11u : 0u))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置截图排除选项。");
    }

    public static nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();
    public static void RestoreForeground(nint window) { if (window != 0) _ = NativeMethods.SetForegroundWindow(window); }
}
