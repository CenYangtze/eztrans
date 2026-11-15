using SkiaSharp;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Layout;

namespace SnapTrans.Core.Imaging;

public static class TextEraser
{
    public static EraseStrategy SelectStrategy(ColorEstimate colors, RenderOptions? options = null)
    {
        options ??= new();
        return colors.StandardDeviation <= options.SolidStandardDeviation ? EraseStrategy.Solid :
            colors.PlaneRms <= options.GradientMaximumRms ? EraseStrategy.Gradient :
            options.EnableInpaint ? EraseStrategy.Inpaint : EraseStrategy.Label;
    }

    public static void Erase(SKBitmap output, TextBlock block, ColorEstimate colors, EraseStrategy strategy,
        IReadOnlyList<PixelRectF>? protectedRegions = null, RenderOptions? options = null, CancellationToken ct = default)
    {
        if (strategy == EraseStrategy.Unchanged) return;
        options ??= new();
        if (strategy == EraseStrategy.Inpaint)
        {
            using var mask = StrokeMask(output, block, colors, options);
            var repair = TeleaInpaint.Apply(output, mask, options.InpaintRadius, protectedRegions, ct);
            if (repair.RemainingPixels > 0) throw new InvalidOperationException("部分笔画缺少可用背景，请改用背景标签擦除。");
            return;
        }
        var pixels = new BitmapPixels(output);
        int pad = Math.Max(options.MinimumPadding, (int)Math.Ceiling(block.LineHeight * options.PaddingRatio));
        foreach (var line in block.Lines)
        {
            var rect = ColorEstimator.Clip(line.Bounds, pad, output.Width, output.Height);
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (int x = rect.Left; x < rect.Right; x++)
                {
                    bool skip = false;
                    if (protectedRegions is not null)
                        foreach (var region in protectedRegions)
                            if (ColorEstimator.Contains(region, x, y)) { skip = true; break; }
                    if (skip) continue;
                    pixels.Set(x, y, strategy == EraseStrategy.Solid ? colors.Background : colors.BackgroundAt(x, y));
                }
            }
        }
        output.NotifyPixelsChanged();
    }

    public static SKBitmap StrokeMask(SKBitmap source, TextBlock block, ColorEstimate colors, RenderOptions? options = null)
    {
        options ??= new();
        var mask = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        mask.Erase(SKColors.Black);
        var inputPixels = new BitmapPixels(source);
        var maskPixels = new BitmapPixels(mask);
        foreach (var line in block.Lines)
        {
            var r = ColorEstimator.Clip(line.Bounds, 0, source.Width, source.Height);
            for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
                if (ColorEstimator.Distance(inputPixels.Get(x, y), colors.BackgroundAt(x, y)) > options.StrokeDistance)
                    for (int yy = Math.Max(0, y - 2); yy < Math.Min(source.Height, y + 3); yy++)
                    for (int xx = Math.Max(0, x - 2); xx < Math.Min(source.Width, x + 3); xx++) maskPixels.Set(xx, yy, SKColors.White);
        }
        mask.NotifyPixelsChanged();
        return mask;
    }
}
