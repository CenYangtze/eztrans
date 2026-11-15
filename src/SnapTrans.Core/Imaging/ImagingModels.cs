using SkiaSharp;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Translation;

namespace SnapTrans.Core.Imaging;

public enum PipelineStage { Ocr, Layout, Translate, Render, Done }
public enum EraseStrategy { Unchanged, Solid, Gradient, Label, Inpaint, Ai }

/// <summary>All image-analysis thresholds use physical pixels.</summary>
public sealed class RenderOptions
{
    public float PaddingRatio { get; set; } = .25f;
    public int MinimumPadding { get; set; } = 2;
    public double SolidStandardDeviation { get; set; } = 6;
    public double GradientMaximumRms { get; set; } = 8;
    public double StrokeDistance { get; set; } = 60;
    public double MinimumContrast { get; set; } = 3;
    public float MinimumFontRatio { get; set; } = .6f;
    public float ExpansionRatio { get; set; } = .15f;
    public float CompressedScaleX { get; set; } = .88f;
    public float WidthTolerance { get; set; } = 1.02f;
    public float HeightTolerance { get; set; } = 1.1f;
    public int MaximumRingSamples { get; set; } = 4096;
    public int FontSearchIterations { get; set; } = 12;
    public bool EnableInpaint { get; set; }
    public int InpaintRadius { get; set; } = 3;
    public bool PreserveBold { get; set; }
    public bool PreserveOutline { get; set; }
    public double BoldMinimumStrokeRatio { get; set; } = .16;
    public double BoldMinimumInkCoverage { get; set; } = .28;
    public double OutlineMinimumBoundaryCoverage { get; set; } = .35;
    public double OutlineMinimumColorDistance { get; set; } = 70;
    public int StyleMinimumSamplePixels { get; set; } = 8;
    public double OutlineColorTolerance { get; set; } = 25;
    public float MaximumOutlineHeightRatio { get; set; } = .15f;
}

public sealed class ImageTranslateOptions
{
    public required TranslateOptions Translation { get; init; }
    public float MinFontPx { get; init; } = 9;
    public IReadOnlyDictionary<string, string> FontMap { get; init; } = new Dictionary<string, string>
    {
        ["zh-Hans"] = "Microsoft YaHei UI", ["zh-Hant"] = "Microsoft JhengHei UI",
        ["ja"] = "Yu Gothic UI", ["ko"] = "Malgun Gothic", ["default"] = "Segoe UI"
    };
    public string EraseMode { get; init; } = "auto";
    public bool AttachContextImage { get; init; } = true;
    public bool DebugDump { get; init; }
    public string? DebugFolder { get; init; }
    public RenderOptions Rendering { get; init; } = new();
    /// <summary>
    /// Optional partial result while later batches are still translating: bitmap, finished batches, total batches.
    /// Called on a worker thread; the bitmap is valid only during the call.
    /// </summary>
    public Action<SKBitmap, int, int>? Preview { get; init; }
}

public interface IImageTranslationPipeline
{
    Task<ImageTranslationResult> RunAsync(SKBitmap source, SnapTrans.Core.Ocr.OcrPage ocr,
        ImageTranslateOptions options, IProgress<PipelineStage>? progress, CancellationToken ct);
}

public sealed record ImageTranslationResult(SKBitmap Output, IReadOnlyList<RenderedBlock> Blocks,
    IReadOnlyList<string> Warnings) : IDisposable
{
    public bool IsAiGenerated { get; init; }
    public void Dispose() => Output.Dispose();
}

public sealed record RenderedBlock(int Id, string Source, string? Translation, EraseStrategy Strategy,
    SKColor TextColor, SKColor BackgroundColor, float FontSize, bool Overflowed,
    bool Bold = false, SKColor? OutlineColor = null, float OutlineWidth = 0);

public readonly record struct ColorPlane(double X, double Y, double Constant)
{
    public byte Evaluate(double x, double y) => (byte)Math.Clamp((int)Math.Round(X * x + Y * y + Constant), 0, 255);
}

public sealed record ColorEstimate(SKColor Background, SKColor Foreground, double StandardDeviation,
    double PlaneRms, ColorPlane Red, ColorPlane Green, ColorPlane Blue)
{
    public SKColor BackgroundAt(int x, int y) => new(Red.Evaluate(x, y), Green.Evaluate(x, y), Blue.Evaluate(x, y));
}

public sealed record FontLayout(PixelRectF Bounds, IReadOnlyList<string> Lines, float FontSize,
    float ScaleX, float LinePitch, float AscentRatio, float InkHeightRatio, bool Overflowed);
