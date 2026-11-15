using SkiaSharp;
using SnapTrans.Core.Llm;

namespace SnapTrans.Core.Translation;

public static class ImageEncoder
{
    public static ImagePart Encode(SKBitmap image, int maxImageLongEdge = 2048, int maxImageBytes = 4 * 1024 * 1024)
    {
        if (maxImageLongEdge < 1 || maxImageBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxImageLongEdge));
        var scale = Math.Min(1d, (double)maxImageLongEdge / Math.Max(image.Width, image.Height));
        var jpeg = false;
        while (true)
        {
            var width = Math.Max(1, (int)Math.Round(image.Width * scale));
            var height = Math.Max(1, (int)Math.Round(image.Height * scale));
            using var resized = image.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque),
                new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? throw new InvalidOperationException("无法缩放截图");
            using var encoded = resized.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, jpeg ? 90 : 100);
            if (encoded.Size <= maxImageBytes) return new ImagePart(encoded.ToArray(), jpeg ? "image/jpeg" : "image/png");
            if (!jpeg) { jpeg = true; continue; }
            if (width == 1 && height == 1) throw new InvalidOperationException("无法将截图编码到服务允许的大小");
            scale *= .75;
        }
    }
}
