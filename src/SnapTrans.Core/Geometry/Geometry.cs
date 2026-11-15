namespace SnapTrans.Core.Geometry;

public readonly record struct PixelPoint(int X, int Y);
public readonly record struct PixelSize(int Width, int Height);
public readonly record struct DipPoint(double X, double Y);
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);
    public bool Contains(PixelPoint point) => point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;
    public bool Contains(PixelRect rectangle) => rectangle.X >= X && rectangle.Y >= Y && rectangle.Right <= Right && rectangle.Bottom <= Bottom;
    public PixelRect Intersect(PixelRect other) => new(Math.Max(X, other.X), Math.Max(Y, other.Y), Math.Max(0, Math.Min(Right, other.Right) - Math.Max(X, other.X)), Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y)));
    public static PixelRect FromPoints(PixelPoint a, PixelPoint b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
}
public readonly record struct PixelRectF(float X, float Y, float Width, float Height)
{
    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public static PixelRectF Union(PixelRectF a, PixelRectF b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.Right, b.Right) - Math.Min(a.X, b.X), Math.Max(a.Bottom, b.Bottom) - Math.Min(a.Y, b.Y));
    public PixelRectF Inflate(float padding) => new(X-padding, Y-padding, Width+padding*2, Height+padding*2);
    public PixelRectF Intersect(PixelRectF other) => new(Math.Max(X,other.X), Math.Max(Y,other.Y), Math.Max(0, Math.Min(Right,other.Right)-Math.Max(X,other.X)), Math.Max(0, Math.Min(Bottom,other.Bottom)-Math.Max(Y,other.Y)));
}

public static class DpiMath
{
    public static int DipToPixel(double dip, uint dpi) { ArgumentOutOfRangeException.ThrowIfZero(dpi); return checked((int)Math.Round(dip * dpi / 96d, MidpointRounding.AwayFromZero)); }
    public static double PixelToDip(int pixel, uint dpi) { ArgumentOutOfRangeException.ThrowIfZero(dpi); return pixel * 96d / dpi; }
    public static PixelPoint LocalToVirtual(DipPoint local, PixelRect monitor, uint dpi) => new(DipToPixel(local.X, dpi)+monitor.X, DipToPixel(local.Y,dpi)+monitor.Y);
    public static DipPoint VirtualToLocal(PixelPoint point, PixelRect monitor, uint dpi) => new(PixelToDip(point.X-monitor.X,dpi),PixelToDip(point.Y-monitor.Y,dpi));
}

public static class PopupPlacement
{
    public static PixelRect Compute(PixelRect selection, PixelSize popupSize, PixelRect workArea, int gap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gap);
        int w = Math.Clamp(popupSize.Width, 1, Math.Max(1, workArea.Width)), h = Math.Clamp(popupSize.Height, 1, Math.Max(1, workArea.Height));
        PixelRect[] candidates = [new(selection.Right+gap,selection.Top,w,h), new(selection.Left,selection.Bottom+gap,w,h), new(selection.Left-gap-w,selection.Top,w,h), new(selection.Left,selection.Top-gap-h,w,h)];
        foreach(var candidate in candidates) if(workArea.Contains(candidate)) return candidate;
        var best = candidates.OrderByDescending(c=>c.Intersect(workArea).Area).First();
        return new(Math.Clamp(best.X,workArea.Left,Math.Max(workArea.Left,workArea.Right-w)),Math.Clamp(best.Y,workArea.Top,Math.Max(workArea.Top,workArea.Bottom-h)),w,h);
    }
}
