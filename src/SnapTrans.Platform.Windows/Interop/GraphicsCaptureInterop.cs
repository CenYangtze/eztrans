using System.ComponentModel;
using System.Runtime.InteropServices;
using SnapTrans.Core.Abstractions;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace SnapTrans.Platform.Windows.Interop;

/// <summary>Minimal D3D11 and WinRT activation boundary; no native handles escape this adapter.</summary>
internal static class GraphicsCaptureInterop
{
    private static readonly Guid DxgiDeviceId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid CaptureItemInteropId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid CaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point(int x, int y) { internal readonly int X = x, Y = y; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForMonitor(nint factory, nint monitor, in Guid interfaceId, out nint item);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out uint featureLevel, out nint immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string text, uint length, out nint value);

    [DllImport("combase.dll", ExactSpelling = true)] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int RoGetActivationFactory(nint name, in Guid interfaceId, out nint factory);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint MonitorFromPoint(Point point, uint flags);

    internal static IDirect3DDevice CreateDevice()
    {
        nint device = 0, context = 0, dxgiDevice = 0, inspectable = 0;
        try
        {
            // Hardware first; WARP is the documented software provider for machines without D3D hardware.
            int result = D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out device, out _, out context);
            if (result < 0)
            {
                Release(ref context); Release(ref device);
                result = D3D11CreateDevice(0, 5, 0, 0x20, 0, 0, 7, out device, out _, out context);
            }
            Marshal.ThrowExceptionForHR(result);
            var id = DxgiDeviceId;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in id, out dxgiDevice));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Release(ref inspectable); Release(ref dxgiDevice); Release(ref context); Release(ref device);
        }
    }

    internal static GraphicsCaptureItem CreateMonitorItem(MonitorInfo monitor)
    {
        var bounds = monitor.Bounds;
        var handle = MonitorFromPoint(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), 0);
        if (handle == 0) throw new InvalidOperationException("显示器已经断开，请重新截图。");
        var info = new NativeMethods.MonitorInfoEx { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfoEx>(), Device = "" };
        if (!NativeMethods.GetMonitorInfo(handle, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!string.Equals(info.Device, monitor.DeviceName, StringComparison.OrdinalIgnoreCase) ||
            info.Monitor.Left != bounds.X || info.Monitor.Top != bounds.Y ||
            info.Monitor.Right - info.Monitor.Left != bounds.Width || info.Monitor.Bottom - info.Monitor.Top != bounds.Height)
            throw new InvalidOperationException("显示器布局已改变，请重新截图。");

        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint runtimeName = 0, factory = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out runtimeName));
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(runtimeName, in CaptureItemInteropId, out factory));
            var vtable = Marshal.ReadIntPtr(factory);
            var create = Marshal.GetDelegateForFunctionPointer<CreateForMonitor>(Marshal.ReadIntPtr(vtable, 4 * nint.Size));
            Marshal.ThrowExceptionForHR(create(factory, handle, in CaptureItemId, out item));
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            Release(ref item); Release(ref factory);
            if (runtimeName != 0) _ = WindowsDeleteString(runtimeName);
        }
    }

    private static void Release(ref nint pointer)
    {
        if (pointer == 0) return;
        _ = Marshal.Release(pointer);
        pointer = 0;
    }
}
