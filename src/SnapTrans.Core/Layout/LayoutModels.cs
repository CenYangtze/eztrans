using SnapTrans.Core.Geometry;
using SnapTrans.Core.Ocr;

namespace SnapTrans.Core.Layout;

public enum TextAlign { Left, Center, Right }
public sealed record TextBlock(int Id,string SourceText,PixelRectF Bounds,IReadOnlyList<OcrLine> Lines,float LineHeight,float? LinePitch,TextAlign Alignment);
public sealed class LayoutOptions
{
    public float WordGapHeightRatio { get; set; } = 1.2f;
    public float RowToleranceRatio { get; set; } = 0.5f;
    public float MaxVerticalGapRatio { get; set; } = 0.9f;
    public float MaxHeightDifferenceRatio { get; set; } = 0.25f;
    public float LeftAlignmentToleranceRatio { get; set; } = 1f;
    public float MinimumHorizontalOverlapRatio { get; set; } = 0.5f;
    public float BandOverlapRatio { get; set; } = 0.5f;
}
