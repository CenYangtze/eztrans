using SkiaSharp;
using SnapTrans.Core.Geometry;

namespace SnapTrans.Core.Imaging;

public readonly record struct InpaintResult(int FilledPixels, int RemainingPixels);

/// <summary>
/// Telea-style fast-marching inpainting. The narrow band advances by the Eikonal distance;
/// each new pixel is estimated from already-known pixels using distance, level-set and
/// arrival-direction weights and a first-order image-gradient correction. No native CV runtime.
/// </summary>
public static class TeleaInpaint
{
    private const byte Known = 0, Band = 1, Unknown = 2;

    public static InpaintResult Apply(SKBitmap image, SKBitmap mask, int radius = 3,
        IReadOnlyList<PixelRectF>? protectedRegions = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(mask);
        if (image.Width != mask.Width || image.Height != mask.Height) throw new ArgumentException("修复掩码与图像尺寸不一致。", nameof(mask));
        if (radius is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(radius), "修复半径须为 1–32 px。");
        ct.ThrowIfCancellationRequested();
        int width = image.Width, height = image.Height, count = checked(width * height), missing = 0;
        var state = new byte[count];
        var arrival = new float[count];
        var pixels = new BitmapPixels(image);
        var maskPixels = new BitmapPixels(mask);
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                var c = maskPixels.Get(x, y);
                if (Math.Max(c.Red, Math.Max(c.Green, c.Blue)) < 128) continue;
                bool protect = false;
                if (protectedRegions is not null)
                    foreach (var region in protectedRegions)
                        if (ColorEstimator.Contains(region, x, y)) { protect = true; break; }
                if (protect) continue;
                int i = y * width + x;
                state[i] = Unknown;
                arrival[i] = float.PositiveInfinity;
                missing++;
            }
        }
        if (missing == 0) return new(0, 0);
        var queue = new PriorityQueue<int, float>();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            if (state[i] != Unknown) continue;
            if ((x > 0 && state[i - 1] == Known) || (x + 1 < width && state[i + 1] == Known) ||
                (y > 0 && state[i - width] == Known) || (y + 1 < height && state[i + width] == Known))
            {
                state[i] = Band;
                arrival[i] = 1;
                queue.Enqueue(i, 1);
            }
        }
        if (queue.Count == 0) throw new InvalidOperationException("修复区域覆盖整幅图像，没有可用背景；请改用背景标签擦除。");
        int filled = 0;
        while (queue.TryDequeue(out int i, out float distance))
        {
            if (state[i] == Known || distance > arrival[i] + 1e-5f) continue;
            ct.ThrowIfCancellationRequested();
            int x = i % width, y = i / width;
            double tx = DistanceGradient(x, y, true, width, height, arrival), ty = DistanceGradient(x, y, false, width, height, arrival);
            double red = 0, green = 0, blue = 0, total = 0;
            for (int yy = Math.Max(0, y - radius); yy <= Math.Min(height - 1, y + radius); yy++)
            for (int xx = Math.Max(0, x - radius); xx <= Math.Min(width - 1, x + radius); xx++)
            {
                int q = yy * width + xx, dx = x - xx, dy = y - yy, square = dx * dx + dy * dy;
                if (state[q] != Known || square == 0 || square > radius * radius) continue;
                double direction = Math.Abs(dx * tx + dy * ty);
                if (direction < .01) direction = .01;
                double weight = direction / (square * Math.Sqrt(square) * (1 + Math.Abs(arrival[i] - arrival[q])));
                var color = pixels.Get(xx, yy);
                var gx = ImageGradient(pixels, xx, yy, true, width, height, state);
                var gy = ImageGradient(pixels, xx, yy, false, width, height, state);
                red += weight * Math.Clamp(color.Red + gx.R * dx + gy.R * dy, 0, 255);
                green += weight * Math.Clamp(color.Green + gx.G * dx + gy.G * dy, 0, 255);
                blue += weight * Math.Clamp(color.Blue + gx.B * dx + gy.B * dy, 0, 255);
                total += weight;
            }
            if (total <= 0) continue;
            pixels.Set(x, y, new((byte)Math.Clamp((int)Math.Round(red / total), 0, 255),
                (byte)Math.Clamp((int)Math.Round(green / total), 0, 255), (byte)Math.Clamp((int)Math.Round(blue / total), 0, 255)));
            state[i] = Known;
            filled++;
            foreach (int next in Neighbors(x, y, width, height))
            {
                if (state[next] == Known) continue;
                float proposed = SolveDistance(next % width, next / width, width, height, state, arrival);
                if (proposed >= arrival[next]) continue;
                arrival[next] = proposed;
                state[next] = Band;
                queue.Enqueue(next, proposed);
            }
        }
        image.NotifyPixelsChanged();
        return new(filled, missing - filled);
    }

    private static IEnumerable<int> Neighbors(int x, int y, int width, int height)
    {
        int i = y * width + x;
        if (x > 0) yield return i - 1;
        if (x + 1 < width) yield return i + 1;
        if (y > 0) yield return i - width;
        if (y + 1 < height) yield return i + width;
    }
    private static float SolveDistance(int x, int y, int width, int height, byte[] state, float[] time)
    {
        int i = y * width + x;
        float left = x > 0 && state[i - 1] == Known ? time[i - 1] : float.PositiveInfinity;
        float right = x + 1 < width && state[i + 1] == Known ? time[i + 1] : float.PositiveInfinity;
        float top = y > 0 && state[i - width] == Known ? time[i - width] : float.PositiveInfinity;
        float bottom = y + 1 < height && state[i + width] == Known ? time[i + width] : float.PositiveInfinity;
        float a = Math.Min(left, right), b = Math.Min(top, bottom);
        if (!float.IsFinite(a)) return b + 1;
        if (!float.IsFinite(b)) return a + 1;
        float difference = Math.Abs(a - b);
        return difference >= 1 ? Math.Min(a, b) + 1 : (a + b + MathF.Sqrt(2 - difference * difference)) / 2;
    }
    private static double DistanceGradient(int x, int y, bool horizontal, int width, int height, float[] time)
    {
        int i = y * width + x;
        bool before = horizontal ? x > 0 : y > 0, after = horizontal ? x + 1 < width : y + 1 < height;
        int delta = horizontal ? 1 : width;
        float center = time[i];
        float a = before && float.IsFinite(time[i - delta]) ? time[i - delta] : center;
        float b = after && float.IsFinite(time[i + delta]) ? time[i + delta] : center;
        return (b - a) / (before && after ? 2d : 1d);
    }
    private static (double R, double G, double B) ImageGradient(BitmapPixels pixels, int x, int y, bool horizontal,
        int width, int height, byte[] state)
    {
        int x0 = horizontal ? x - 1 : x, y0 = horizontal ? y : y - 1;
        int x1 = horizontal ? x + 1 : x, y1 = horizontal ? y : y + 1;
        bool a = x0 >= 0 && y0 >= 0 && state[y0 * width + x0] == Known;
        bool b = x1 < width && y1 < height && state[y1 * width + x1] == Known;
        var ca = a ? pixels.Get(x0, y0) : pixels.Get(x, y);
        var cb = b ? pixels.Get(x1, y1) : pixels.Get(x, y);
        double divisor = a && b ? 2 : 1;
        return ((cb.Red - ca.Red) / divisor, (cb.Green - ca.Green) / divisor, (cb.Blue - ca.Blue) / divisor);
    }
}
