using SkiaSharp;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Layout;

namespace SnapTrans.Core.Imaging;

public static class ColorEstimator
{
    public static ColorEstimate Estimate(SKBitmap image, TextBlock block, RenderOptions? options = null)
    {
        options ??= new();
        var pixels = new BitmapPixels(image);
        var b = block.Bounds;
        int pad = Math.Max(options.MinimumPadding, (int)Math.Ceiling(block.LineHeight * options.PaddingRatio));
        var outer = Clip(b, pad, image.Width, image.Height);
        var ring = new List<(int X, int Y, SKColor C)>();
        int approximateCount = Math.Max(1, outer.Width * outer.Height - (int)(b.Width * b.Height));
        int stride = Math.Max(1, (int)Math.Sqrt((double)approximateCount / options.MaximumRingSamples));
        for (int y = outer.Top; y < outer.Bottom; y += stride)
        for (int x = outer.Left; x < outer.Right; x += stride)
            if (!Contains(b, x, y)) ring.Add((x, y, pixels.Get(x, y)));
        // A block can occupy the whole crop: use the perimeter instead of an empty ring.
        if (ring.Count == 0)
        {
            for (int x = 0; x < image.Width; x += stride)
            {
                ring.Add((x, 0, pixels.Get(x, 0)));
                ring.Add((x, image.Height - 1, pixels.Get(x, image.Height - 1)));
            }
            for (int y = 0; y < image.Height; y += stride)
            {
                ring.Add((0, y, pixels.Get(0, y)));
                ring.Add((image.Width - 1, y, pixels.Get(image.Width - 1, y)));
            }
        }
        var background = Median(ring.Select(p => p.C));
        double mean = ring.Average(p => Brightness(p.C));
        double sd = Math.Sqrt(ring.Average(p => Math.Pow(Brightness(p.C) - mean, 2)));
        var red = Fit(ring, p => p.Red, background.Red);
        var green = Fit(ring, p => p.Green, background.Green);
        var blue = Fit(ring, p => p.Blue, background.Blue);
        double rms = Math.Sqrt(ring.Average(p =>
            (Math.Pow(red.X * p.X + red.Y * p.Y + red.Constant - p.C.Red, 2) +
             Math.Pow(green.X * p.X + green.Y * p.Y + green.Constant - p.C.Green, 2) +
             Math.Pow(blue.X * p.X + blue.Y * p.Y + blue.Constant - p.C.Blue, 2)) / 3));
        var inner = Clip(b, 0, image.Width, image.Height);
        var strokes = new List<(double Distance, SKColor Color)>();
        for (int y = inner.Top; y < inner.Bottom; y++)
        for (int x = inner.Left; x < inner.Right; x++)
        {
            var color = pixels.Get(x, y);
            var localBackground = new SKColor(red.Evaluate(x, y), green.Evaluate(x, y), blue.Evaluate(x, y));
            double distance = Distance(color, localBackground);
            if (distance > options.StrokeDistance) strokes.Add((distance, color));
        }
        var foreground = strokes.Count == 0 ? BestContrast(background) :
            Median(strokes.OrderByDescending(x => x.Distance).Take(Math.Max(1, (int)Math.Ceiling(strokes.Count * .3))).Select(x => x.Color));
        if (Contrast(foreground, background) < options.MinimumContrast) foreground = BestContrast(background);
        return new(background, foreground, sd, rms, red, green, blue);
    }

    public static double Distance(SKColor a, SKColor b) => Math.Sqrt(
        Math.Pow(a.Red - b.Red, 2) + Math.Pow(a.Green - b.Green, 2) + Math.Pow(a.Blue - b.Blue, 2));
    public static double Contrast(SKColor a, SKColor b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + .05) / (Math.Min(la, lb) + .05);
    }
    public static SKColor BestContrast(SKColor background) =>
        Contrast(SKColors.Black, background) >= Contrast(SKColors.White, background) ? SKColors.Black : SKColors.White;
    internal static SKRectI Clip(PixelRectF b, int padding, int width, int height) => new(
        Math.Clamp((int)Math.Floor(b.X) - padding, 0, width), Math.Clamp((int)Math.Floor(b.Y) - padding, 0, height),
        Math.Clamp((int)Math.Ceiling(b.X + b.Width) + padding, 0, width), Math.Clamp((int)Math.Ceiling(b.Y + b.Height) + padding, 0, height));
    internal static bool Contains(PixelRectF b, int x, int y) => x >= b.X && y >= b.Y && x < b.X + b.Width && y < b.Y + b.Height;
    internal static SKRect Rect(PixelRectF b) => new(b.X, b.Y, b.X + b.Width, b.Y + b.Height);
    private static double Brightness(SKColor c) => .2126 * c.Red + .7152 * c.Green + .0722 * c.Blue;
    private static double Luminance(SKColor c)
    {
        static double Linear(byte channel) { double v = channel / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Linear(c.Red) + .7152 * Linear(c.Green) + .0722 * Linear(c.Blue);
    }
    private static SKColor Median(IEnumerable<SKColor> source)
    {
        var colors = source.ToArray();
        if (colors.Length == 0) return SKColors.White;
        static byte Mid(IEnumerable<byte> values) { var a = values.Order().ToArray(); return a[a.Length / 2]; }
        return new(Mid(colors.Select(c => c.Red)), Mid(colors.Select(c => c.Green)), Mid(colors.Select(c => c.Blue)));
    }
    private static ColorPlane Fit(List<(int X, int Y, SKColor C)> points, Func<SKColor, byte> channel, byte fallback)
    {
        // Center coordinates before solving the normal equations, avoiding ill-conditioned screen coordinates.
        double mx = points.Average(p => p.X), my = points.Average(p => p.Y), mc = points.Average(p => channel(p.C));
        double xx = 0, xy = 0, yy = 0, xc = 0, yc = 0;
        foreach (var p in points)
        {
            double x = p.X - mx, y = p.Y - my, c = channel(p.C) - mc;
            xx += x * x; xy += x * y; yy += y * y; xc += x * c; yc += y * c;
        }
        double det = xx * yy - xy * xy;
        if (Math.Abs(det) < 1e-8) return new(0, 0, fallback);
        double a = (xc * yy - yc * xy) / det, b = (yc * xx - xc * xy) / det;
        return new(a, b, mc - a * mx - b * my);
    }
}
