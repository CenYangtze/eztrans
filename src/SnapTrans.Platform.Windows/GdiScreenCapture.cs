using System.ComponentModel;
using System.Runtime.InteropServices;
using SkiaSharp;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Geometry;
using SnapTrans.Platform.Windows.Interop;

namespace SnapTrans.Platform.Windows;

public sealed class GdiScreenCapture : IScreenCapture
{
    public IReadOnlyList<MonitorCapture> CaptureAll(IReadOnlyList<MonitorInfo> monitors)
    {
        var captures = new List<MonitorCapture>(monitors.Count);
        try
        {
            // This synchronous call must finish before any overlay is shown.
            foreach (var monitor in monitors) captures.Add(new(monitor, Capture(monitor.Bounds)));
            return captures;
        }
        catch { foreach (var capture in captures) capture.Bitmap.Dispose(); throw; }
    }

    public SKBitmap CaptureRegion(PixelRect region) => Capture(region);

    private static SKBitmap Capture(PixelRect bounds)
    {
        if (bounds.IsEmpty) throw new ArgumentException("截图区域为空。", nameof(bounds));
        var bytes = checked(bounds.Width * bounds.Height * 4);
        var screen = NativeMethods.GetDC(0);
        if (screen == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint memory = 0, dib = 0, previous = 0;
        try
        {
            memory = NativeMethods.CreateCompatibleDC(screen);
            if (memory == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var header = new NativeMethods.BitmapInfoHeader
            {
                Size = 40, Width = bounds.Width, Height = -bounds.Height, Planes = 1, BitCount = 32,
                Compression = 0, SizeImage = (uint)bytes
            };
            dib = NativeMethods.CreateDIBSection(screen, ref header, 0, out var pixels, 0, 0);
            if (dib == 0 || pixels == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = NativeMethods.SelectObject(memory, dib);
            if (previous == 0 || previous == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!NativeMethods.BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, NativeMethods.Srccopy | NativeMethods.CaptureBlt))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法捕获屏幕。");
            var bitmap = new SKBitmap(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            try
            {
                CopyOpaquePixels(pixels, bitmap.GetPixels(), bytes);
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        finally
        {
            if (previous != 0 && previous != -1 && memory != 0) NativeMethods.SelectObject(memory, previous);
            if (dib != 0) NativeMethods.DeleteObject(dib);
            if (memory != 0) NativeMethods.DeleteDC(memory);
            NativeMethods.ReleaseDC(0, screen);
        }
    }

    private static unsafe void CopyOpaquePixels(nint source, nint destination, int byteCount)
    {
        // Avoid retaining a 33 MB managed/pool buffer for every captured 4K monitor.
        Buffer.MemoryCopy((void*)source, (void*)destination, byteCount, byteCount);
        var bytes = new Span<byte>((void*)destination, byteCount);
        // GDI does not write alpha; opaque interpretation and explicit alpha also protect
        // subsequent WinRT premultiplied copies from turning the image transparent.
        for (int index = 3; index < bytes.Length; index += 4) bytes[index] = 255;
    }
}
