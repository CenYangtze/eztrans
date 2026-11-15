using SkiaSharp;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Layout;

namespace SnapTrans.Core.Imaging;

public static class TextRenderer
{
    public static void Draw(SKCanvas canvas, string text, TextBlock block, FontLayout layout, FontResolver fonts,
        ColorEstimate colors, EraseStrategy strategy, IReadOnlyList<PixelRectF>? protectedRegions = null, TextStyleEstimate? style = null)
    {
        canvas.Save();
        try
        {
            if (protectedRegions is not null)
                foreach (var region in protectedRegions) canvas.ClipRect(ColorEstimator.Rect(region), SKClipOperation.Difference);
            using var paint = new SKPaint { Color = style?.FillColor ?? colors.Foreground, IsAntialias = true };
            using var outline = style?.OutlineColor is { } outlineColor ? new SKPaint
            {
                Color = outlineColor, IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = 2 * style.OutlineWidth * layout.InkHeightRatio * layout.FontSize / Math.Max(1, block.LineHeight), StrokeJoin = SKStrokeJoin.Round
            } : null;
            if (strategy == EraseStrategy.Label)
            {
                using var background = new SKPaint { Color = colors.Background.WithAlpha(217), IsAntialias = true };
                canvas.DrawRoundRect(ColorEstimator.Rect(layout.Bounds), 3, 3, background);
            }
            float top = layout.Bounds.Y;
            if (block.Lines.Count == 1 && layout.Lines.Count == 1)
                top += Math.Max(0, (layout.Bounds.Height - layout.InkHeightRatio * layout.FontSize) / 2);
            float baseline = top + layout.AscentRatio * layout.FontSize;
            foreach (string line in layout.Lines)
            {
                float width = fonts.Measure(line, layout.FontSize, layout.ScaleX);
                float x = block.Alignment switch
                {
                    TextAlign.Center => layout.Bounds.X + (layout.Bounds.Width - width) / 2,
                    TextAlign.Right => layout.Bounds.X + layout.Bounds.Width - width,
                    _ => layout.Bounds.X
                };
                foreach (var run in fonts.Runs(line))
                {
                    using var font = FontResolver.CreateFont(run.Typeface, layout.FontSize, layout.ScaleX);
                    if (outline is not null) canvas.DrawText(run.Text, x, baseline, font, outline);
                    canvas.DrawText(run.Text, x, baseline, font, paint);
                    x += font.MeasureText(run.Text);
                }
                baseline += layout.LinePitch;
            }
        }
        finally { canvas.Restore(); }
    }
}
