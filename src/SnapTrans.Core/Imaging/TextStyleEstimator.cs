using SkiaSharp;
using SnapTrans.Core.Layout;

namespace SnapTrans.Core.Imaging;

public sealed record TextStyleEstimate(bool Bold, SKColor FillColor, SKColor? OutlineColor, float OutlineWidth,
    double InkCoverage, double StrokeRatio);

/// <summary>Conservative local stroke analysis. Style restoration is opt-in because OCR boxes can contain icons.</summary>
public static class TextStyleEstimator
{
    public static TextStyleEstimate Estimate(SKBitmap source, TextBlock block, ColorEstimate colors, RenderOptions? options = null)
    {
        options ??= new();
        var b = ColorEstimator.Clip(block.Bounds, 0, source.Width, source.Height);
        int width = b.Width, height = b.Height;
        if (width <= 0 || height <= 0) return new(false, colors.Foreground, null, 0, 0, 0);
        int count = checked(width * height), inkCount = 0;
        var ink = new bool[count];
        var distance = new int[count];
        var pixels = new BitmapPixels(source);
        const int infinity = 1_000_000;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            bool insideLine = block.Lines.Any(line => ColorEstimator.Contains(line.Bounds, b.Left + x, b.Top + y));
            ink[i] = insideLine && ColorEstimator.Distance(pixels.Get(b.Left + x, b.Top + y), colors.BackgroundAt(b.Left + x, b.Top + y)) > options.StrokeDistance;
            distance[i] = ink[i] ? infinity : 0;
            if (ink[i]) inkCount++;
        }
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            if (!ink[i]) continue;
            int a = x > 0 ? distance[i - 1] : 0, c = y > 0 ? distance[i - width] : 0;
            distance[i] = Math.Min(distance[i], Math.Min(a, c) + 1);
        }
        for (int y = height - 1; y >= 0; y--)
        for (int x = width - 1; x >= 0; x--)
        {
            int i = y * width + x;
            if (!ink[i]) continue;
            int a = x + 1 < width ? distance[i + 1] : 0, c = y + 1 < height ? distance[i + width] : 0;
            distance[i] = Math.Min(distance[i], Math.Min(a, c) + 1);
        }
        var maxima = new List<int>();
        var border = new List<SKColor>();
        var core = new List<SKColor>();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            if (!ink[i]) continue;
            var color = pixels.Get(b.Left + x, b.Top + y);
            if (distance[i] == 1) border.Add(color); else core.Add(color);
            if ((x == 0 || distance[i] >= distance[i - 1]) && (x + 1 == width || distance[i] >= distance[i + 1]) &&
                (y == 0 || distance[i] >= distance[i - width]) && (y + 1 == height || distance[i] >= distance[i + width])) maxima.Add(distance[i]);
        }
        double coverage = (double)inkCount / count;
        double ratio = maxima.Count == 0 ? 0 : 2d * maxima.Order().ElementAt(maxima.Count / 2) / Math.Max(1, block.LineHeight);
        bool bold = options.PreserveBold && coverage >= options.BoldMinimumInkCoverage && ratio >= options.BoldMinimumStrokeRatio;
        SKColor? outline = null;
        SKColor fill = colors.Foreground;
        float outlineWidth = 0;
        if (options.PreserveOutline && border.Count >= options.StyleMinimumSamplePixels && core.Count >= options.StyleMinimumSamplePixels)
        {
            var edgeColor = Median(border);
            var coreColor = Median(core);
            double stableBorder = (double)border.Count(c => ColorEstimator.Distance(c, edgeColor) <= options.OutlineColorTolerance) / border.Count;
            // A genuine outline has its own stable color and surrounds a different inner fill.
            if (stableBorder >= options.OutlineMinimumBoundaryCoverage &&
                ColorEstimator.Distance(edgeColor, coreColor) >= options.OutlineMinimumColorDistance &&
                ColorEstimator.Distance(edgeColor, colors.Background) > options.StrokeDistance)
            {
                outline = edgeColor;
                fill = coreColor;
                var depths = new List<int>();
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (ink[i] && ColorEstimator.Distance(pixels.Get(b.Left + x, b.Top + y), edgeColor) <= options.OutlineColorTolerance) depths.Add(distance[i]);
                }
                outlineWidth = depths.Count == 0 ? 1 : Math.Clamp(depths.Order().ElementAt(depths.Count / 2), 1, Math.Max(1, block.LineHeight * options.MaximumOutlineHeightRatio));
            }
        }
        return new(bold, fill, outline, outlineWidth, coverage, ratio);
    }

    private static SKColor Median(List<SKColor> colors)
    {
        byte r = colors.Select(c => c.Red).Order().ElementAt(colors.Count / 2);
        byte g = colors.Select(c => c.Green).Order().ElementAt(colors.Count / 2);
        byte b = colors.Select(c => c.Blue).Order().ElementAt(colors.Count / 2);
        return new(r, g, b);
    }
}
