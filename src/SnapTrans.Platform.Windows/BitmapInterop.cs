using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace SnapTrans.Platform.Windows;

public static class BitmapInterop
{
    public static BitmapSource ToBitmapSource(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using var converted = bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Bgra8888);
        var source = converted ?? bitmap;
        // Bgr32 ignores GDI's otherwise undefined alpha and avoids an expensive PNG round-trip.
        var result = BitmapSource.Create(source.Width, source.Height, 96, 96, PixelFormats.Bgr32, null,
            source.GetPixels(), source.ByteCount, source.RowBytes);
        result.Freeze();
        return result;
    }
}
