using SnapTrans.Core.Geometry;
using SnapTrans.Core.Layout;

namespace SnapTrans.Core.Imaging;

public static class FontFitter
{
    public static FontLayout Fit(string text, TextBlock block, IReadOnlyList<TextBlock> blocks,
        int imageWidth, int imageHeight, FontResolver font, string language, float minimumFontPx = 9, RenderOptions? options = null)
    {
        options ??= new();
        var (ascent, descent) = font.InkRatios(language);
        float inkHeight = Math.Max(.1f, ascent + descent);
        float max = Math.Max(1, block.LineHeight / inkHeight);
        float min = Math.Min(max, Math.Max(minimumFontPx, options.MinimumFontRatio * max));
        var bounds = Expand(block, blocks, imageWidth, imageHeight, options.ExpansionRatio);
        bool originalSingleLine = block.Lines.Count == 1;

        FontLayout? Search(PixelRectF available, float scale, bool wrap)
        {
            FontLayout Layout(float size)
            {
                float pitch = block.LinePitch is { } p ? Math.Clamp(p * size / max, 1.1f * size, 1.8f * size) : 1.3f * size;
                var lines = wrap ? LineBreaker.Break(text, Math.Max(1, available.Width), s => font.Measure(s, size, scale)) : [text.Replace('\n', ' ')];
                bool overflow = lines.Any(l => font.Measure(l, size, scale) > options.WidthTolerance * available.Width) ||
                    (lines.Count - 1) * pitch + inkHeight * size > options.HeightTolerance * available.Height;
                return new(available, lines, size, scale, pitch, ascent, inkHeight, overflow);
            }
            if (Layout(min).Overflowed) return null;
            float low = min, high = max;
            for (int i = 0; i < options.FontSearchIterations; i++)
            {
                float middle = (low + high) / 2;
                if (Layout(middle).Overflowed) high = middle; else low = middle;
            }
            return Layout(low);
        }

        FontLayout? fitted = originalSingleLine ? Search(bounds, 1, false) : null;
        fitted ??= Search(bounds, 1, true);
        fitted ??= originalSingleLine ? Search(bounds, options.CompressedScaleX, false) : null;
        fitted ??= Search(bounds, options.CompressedScaleX, true);
        if (fitted is not null) return fitted;
        var extended = ExpandDown(bounds, block.Id, blocks, imageHeight);
        fitted = Search(extended, options.CompressedScaleX, true);
        if (fitted is not null) return fitted;
        float fallbackPitch = block.LinePitch is { } pitch ? Math.Clamp(pitch * min / max, 1.1f * min, 1.8f * min) : 1.3f * min;
        return new(extended, LineBreaker.Break(text, Math.Max(1, extended.Width), s => font.Measure(s, min, options.CompressedScaleX)),
            min, options.CompressedScaleX, fallbackPitch, ascent, inkHeight, true);
    }

    public static PixelRectF Expand(TextBlock block, IReadOnlyList<TextBlock> blocks, int width, int height, float ratio)
    {
        var b = block.Bounds;
        float right = Math.Min(width, b.X + b.Width * (1 + ratio));
        float bottom = Math.Min(height, b.Y + b.Height * (1 + ratio));
        foreach (var other in blocks.Where(x => x.Id != block.Id))
        {
            var o = other.Bounds;
            if (o.Y < bottom && o.Y + o.Height > b.Y && o.X >= b.X + b.Width) right = Math.Min(right, o.X);
            if (o.X < right && o.X + o.Width > b.X && o.Y >= b.Y + b.Height) bottom = Math.Min(bottom, o.Y);
        }
        return new(b.X, b.Y, Math.Max(1, right - b.X), Math.Max(1, bottom - b.Y));
    }

    private static PixelRectF ExpandDown(PixelRectF b, int id, IReadOnlyList<TextBlock> blocks, int height)
    {
        float bottom = height;
        foreach (var other in blocks.Where(x => x.Id != id))
            if (other.Bounds.X < b.X + b.Width && other.Bounds.X + other.Bounds.Width > b.X && other.Bounds.Y >= b.Y + b.Height)
                bottom = Math.Min(bottom, other.Bounds.Y);
        return new(b.X, b.Y, b.Width, Math.Max(b.Height, bottom - b.Y));
    }
}
