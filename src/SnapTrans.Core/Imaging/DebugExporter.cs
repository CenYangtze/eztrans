using System.Text.Json;
using SkiaSharp;
using SnapTrans.Core.Layout;
using SnapTrans.Core.Ocr;

namespace SnapTrans.Core.Imaging;

/// <summary>Explicit opt-in only: exports screenshots and recognized text for diagnosing rendering.</summary>
public static class DebugExporter
{
    public static async Task<string> ExportAsync(SKBitmap source, OcrPage ocr, IReadOnlyList<TextBlock> blocks,
        ImageTranslationResult result, ImageTranslateOptions options, CancellationToken ct = default)
    {
        if (!options.DebugDump) throw new InvalidOperationException("调试导出未开启。");
        string root = options.DebugFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapTrans", "debug");
        string folder = Path.Combine(root, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        await SaveAsync(source, Path.Combine(folder, "original.png"), ct).ConfigureAwait(false);
        await SaveAsync(result.Output, Path.Combine(folder, "result.png"), ct).ConfigureAwait(false);
        using var ocrBoxes = ImageComposer.Copy(source);
        using var blockBoxes = ImageComposer.Copy(source);
        using var mask = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        mask.Erase(SKColors.Black);
        using (var canvas = new SKCanvas(ocrBoxes))
        using (var paint = new SKPaint { Color = SKColors.OrangeRed, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
            foreach (var line in ocr.Lines) canvas.DrawRect(ColorEstimator.Rect(line.Bounds), paint);
        using (var canvas = new SKCanvas(blockBoxes))
        using (var paint = new SKPaint { Color = SKColors.DodgerBlue, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
        using (var label = new SKPaint { Color = SKColors.Red, IsAntialias = true })
        using (var font = new SKFont(SKTypeface.Default, 14))
        {
            foreach (var block in blocks)
            {
                canvas.DrawRect(ColorEstimator.Rect(block.Bounds), paint);
                canvas.DrawText(block.Id.ToString(), block.Bounds.X, Math.Max(14, block.Bounds.Y), font, label);
                if (result.Blocks.Any(x => x.Id == block.Id && x.Strategy != EraseStrategy.Unchanged))
                {
                    var colors = ColorEstimator.Estimate(source, block, options.Rendering);
                    using var blockMask = TextEraser.StrokeMask(source, block, colors, options.Rendering);
                    using var maskCanvas = new SKCanvas(mask);
                    using var maskPaint = new SKPaint { BlendMode = SKBlendMode.Lighten };
                    maskCanvas.DrawBitmap(blockMask, 0, 0, maskPaint);
                }
            }
        }
        await SaveAsync(ocrBoxes, Path.Combine(folder, "ocr-boxes.png"), ct).ConfigureAwait(false);
        await SaveAsync(blockBoxes, Path.Combine(folder, "blocks.png"), ct).ConfigureAwait(false);
        await SaveAsync(mask, Path.Combine(folder, "erase-mask.png"), ct).ConfigureAwait(false);
        var details = new { Width = source.Width, Height = source.Height, Blocks = result.Blocks, result.Warnings };
        await File.WriteAllTextAsync(Path.Combine(folder, "blocks.json"), JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
        return folder;
    }

    private static Task SaveAsync(SKBitmap image, string path, CancellationToken ct) => File.WriteAllBytesAsync(path, ImageComposer.EncodePng(image), ct);
}
