using System.Runtime.InteropServices;

namespace SnapTrans.Platform.Windows.Interop;

// All unmanaged declarations are kept here so physical-pixel boundary handling can be audited.
internal static class NativeMethods
{
    internal const uint Srccopy = 0x00CC0020, CaptureBlt = 0x40000000;
    internal const int WmHotkey = 0x0312;
    internal delegate bool MonitorEnumProc(nint monitor, nint dc, ref Rect rect, nint data);
    internal delegate bool WindowEnumProc(nint window, nint data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { internal int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        internal uint Size;
        internal Rect Monitor, Work;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, SizeImage;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)] internal static extern nint GetDC(nint window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, uint size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static extern int DwmGetWindowRectAttribute(nint window, uint attribute, out Rect value, uint size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static extern int DwmGetWindowIntAttribute(nint window, uint attribute, out int value, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(WindowEnumProc callback, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] internal static extern nint GetShellWindow();
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] internal static extern nint GetTopWindow(nint window);
}
