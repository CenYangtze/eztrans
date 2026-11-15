using SkiaSharp;
using System.Runtime.InteropServices;
using SnapTrans.Core.Abstractions;

namespace SnapTrans.Platform.Windows;

/// <summary>GDI remains the normal fast path; a completely black frame can request WGC once.</summary>
public sealed class ScreenCaptureService : IScreenCapture
{
    private readonly GdiScreenCapture gdi;
    private readonly WindowsGraphicsCapture graphics;
    public ScreenCaptureService() : this(new GdiScreenCapture(), new WindowsGraphicsCapture()) { }
    public ScreenCaptureService(GdiScreenCapture gdi, WindowsGraphicsCapture graphics) { this.gdi = gdi; this.graphics = graphics; }
    public IReadOnlyList<MonitorCapture> CaptureAll(IReadOnlyList<MonitorInfo> monitors) => gdi.CaptureAll(monitors);
    public SKBitmap CaptureRegion(SnapTrans.Core.Geometry.PixelRect region) => gdi.CaptureRegion(region);

    public async Task<IReadOnlyList<MonitorCapture>> CaptureAllAsync(IReadOnlyList<MonitorInfo> monitors, bool useFallback, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Deliberately synchronous here, before returning to the caller: all normal GDI images
        // freeze together on the UI thread before a selection overlay can become visible.
        var frozen = gdi.CaptureAll(monitors).ToList();
        try
        {
            if (!useFallback || !graphics.IsSupported) return frozen;
            for (int index = 0; index < frozen.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsBlack(frozen[index].Bitmap)) continue;
                var replacement = await graphics.CaptureMonitorAsync(frozen[index].Monitor, ct).ConfigureAwait(false);
                frozen[index].Bitmap.Dispose();
                frozen[index] = new(frozen[index].Monitor, replacement);
            }
            return frozen;
        }
        catch { foreach (var capture in frozen) capture.Bitmap.Dispose(); throw; }
    }

    public static bool IsBlack(SKBitmap bitmap)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0) return true;
        // A full scan avoids treating a dark application with a few bright glyphs as protected content.
        if (bitmap.ColorType is SKColorType.Bgra8888 or SKColorType.Rgba8888)
        {
            var data = bitmap.GetPixelSpan();
            for (int y = 0; y < bitmap.Height; y++)
                foreach (uint pixel in MemoryMarshal.Cast<byte, uint>(data.Slice(y * bitmap.RowBytes, bitmap.Width * 4)))
                    if ((pixel & 0x00FCFCFCu) != 0) return false;
            return true;
        }
        using var pixels = bitmap.PeekPixels();
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var color = pixels.GetPixelColor(x, y);
                if (color.Red > 3 || color.Green > 3 || color.Blue > 3) return false;
            }
        return true;
    }
}
