using System.Text;
using SkiaSharp;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Layout;
using SnapTrans.Core.Ocr;
using SnapTrans.Core.Translation;

namespace SnapTrans.Core.Imaging;

/// <summary>F-50: layout, translate, erase all lines, then render all translations.</summary>
public sealed class ImageTranslationPipeline(ISegmentTranslator translator) : IImageTranslationPipeline
{
    public async Task<ImageTranslationResult> RunAsync(SKBitmap source, OcrPage ocr, ImageTranslateOptions options,
        IProgress<PipelineStage>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width != ocr.Width || source.Height != ocr.Height)
            throw new ArgumentException("OCR 坐标与截图尺寸不一致。", nameof(ocr));
        ct.ThrowIfCancellationRequested();
        progress?.Report(PipelineStage.Layout);
        var blocks = new BlockBuilder().Build(ocr);
        if (ocr.TextAngleDeg is { } angle && Math.Abs(angle) > 3)
        {
            progress?.Report(PipelineStage.Done);
            return new(ImageComposer.Copy(source), blocks.Select(b => Unchanged(b, null)).ToArray(), ["旋转或竖排文字暂不支持译图，已保留原图。"]);
        }
        progress?.Report(PipelineStage.Translate);
        SegmentResult translated;
        if (blocks.Count == 0) translated = new(new Dictionary<int, string>(), []);
        else if (options.Preview is { } preview && translator is IProgressiveSegmentTranslator progressive)
        {
            // Finished batches are rendered while later requests are still running.
            var pump = new PreviewPump(snapshot =>
            {
                using var partial = RenderTranslated(source, blocks, snapshot.Translations, options, ct);
                preview(partial.Output, snapshot.CompletedBatches, snapshot.TotalBatches);
            });
            try
            {
                translated = await progressive.TranslateAsync(blocks, options.AttachContextImage ? source : null, options.Translation,
                    snapshot => { if (snapshot.CompletedBatches < snapshot.TotalBatches) pump.Post(snapshot); }, ct).ConfigureAwait(false);
            }
            finally { await pump.StopAsync().ConfigureAwait(false); }
        }
        else translated = await translator.TranslateAsync(blocks, options.AttachContextImage ? source : null, options.Translation, ct).ConfigureAwait(false);
        progress?.Report(PipelineStage.Render);
        var result = await Task.Run(() => RenderTranslated(source, blocks, translated.Translations, options, ct), ct).ConfigureAwait(false);
        try
        {
            if (options.DebugDump)
            {
                try { await DebugExporter.ExportAsync(source, ocr, blocks, result, options, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // Optional diagnostics must not discard an otherwise successful translation.
                    result = result with { Warnings = [.. result.Warnings, "译图已完成，但调试导出失败，请检查磁盘空间和目录权限。"] };
                }
            }
            progress?.Report(PipelineStage.Done);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>Deterministic offline render entry point used by golden/synthetic image tests.</summary>
    public static ImageTranslationResult RenderTranslated(SKBitmap source, IReadOnlyList<TextBlock> blocks,
        IReadOnlyDictionary<int, string> translations, ImageTranslateOptions options, CancellationToken ct = default)
    {
        if (source.Width <= 0 || source.Height <= 0) throw new ArgumentException("截图不能为空。", nameof(source));
        if (!(options.MinFontPx > 0) || !float.IsFinite(options.MinFontPx)) throw new ArgumentOutOfRangeException(nameof(options));
        var output = ImageComposer.Copy(source);
        var results = new List<RenderedBlock>();
        var warnings = new List<string>();
        var plans = new List<(TextBlock Block, string Text, ColorEstimate Colors, EraseStrategy Strategy, TextStyleEstimate? Style)>();
        var protectedRegions = new List<PixelRectF>();
        var resolvers = new Dictionary<(string Family, bool Bold), FontResolver>();
        try
        {
            int missing = 0;
            foreach (var block in blocks)
            {
                ct.ThrowIfCancellationRequested();
                bool hasTranslation = translations.TryGetValue(block.Id, out string? text) && !string.IsNullOrWhiteSpace(text);
                if (!hasTranslation || Normalize(text!) == Normalize(block.SourceText))
                {
                    if (!hasTranslation) missing++;
                    protectedRegions.Add(block.Bounds);
                    results.Add(Unchanged(block, text));
                    continue;
                }
                var colors = ColorEstimator.Estimate(source, block, options.Rendering);
                var strategy = options.EraseMode.ToLowerInvariant() switch
                {
                    "solid" => EraseStrategy.Solid, "gradient" => EraseStrategy.Gradient, "label" => EraseStrategy.Label,
                    "inpaint" => EraseStrategy.Inpaint,
                    _ => TextEraser.SelectStrategy(colors, options.Rendering)
                };
                var style = options.Rendering.PreserveBold || options.Rendering.PreserveOutline
                    ? TextStyleEstimator.Estimate(source, block, colors, options.Rendering) : null;
                plans.Add((block, text!, colors, strategy, style));
            }
            if (missing > 0) warnings.Add($"{missing} 段未翻译，已保留原文。");
            if (blocks.Count == 0) warnings.Add("未识别到文字。");
            foreach (var plan in plans)
                TextEraser.Erase(output, plan.Block, plan.Colors, plan.Strategy, protectedRegions, options.Rendering, ct);
            using (var canvas = new SKCanvas(output))
            {
                foreach (var plan in plans)
                {
                    ct.ThrowIfCancellationRequested();
                    string language = options.Translation.TargetLanguage;
                    string family = ResolveFontFamily(options.FontMap, language);
                    var fontKey = (family, plan.Style?.Bold == true);
                    if (!resolvers.TryGetValue(fontKey, out var font)) resolvers[fontKey] = font = new FontResolver(family, fontKey.Item2);
                    var layout = FontFitter.Fit(plan.Text, plan.Block, blocks, source.Width, source.Height, font, language, options.MinFontPx, options.Rendering);
                    TextRenderer.Draw(canvas, plan.Text, plan.Block, layout, font, plan.Colors, plan.Strategy, protectedRegions, plan.Style);
                    results.Add(new(plan.Block.Id, plan.Block.SourceText, plan.Text, plan.Strategy,
                        plan.Style?.FillColor ?? plan.Colors.Foreground, plan.Colors.Background, layout.FontSize, layout.Overflowed,
                        plan.Style?.Bold == true, plan.Style?.OutlineColor, plan.Style?.OutlineWidth ?? 0));
                }
            }
            if (results.Any(b => b.Overflowed)) warnings.Add("部分译文超出原区域。");
            return new(output, results.OrderBy(x => x.Id).ToArray(), warnings);
        }
        catch { output.Dispose(); throw; }
        finally { foreach (var resolver in resolvers.Values) resolver.Dispose(); }
    }

    public static string ResolveFontFamily(IReadOnlyDictionary<string, string> map, string language)
    {
        if (map.TryGetValue(language, out string? exact)) return exact;
        string prefix = language.Split('-')[0];
        if (map.TryGetValue(prefix, out string? parent)) return parent;
        return map.TryGetValue("default", out string? fallback) ? fallback : "Segoe UI";
    }
    private static string Normalize(string value) => string.Join(' ', value.Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static RenderedBlock Unchanged(TextBlock block, string? translation) =>
        new(block.Id, block.SourceText, translation, EraseStrategy.Unchanged, SKColors.Transparent, SKColors.Transparent, 0, false);
}

/// <summary>Renders only the newest snapshot on one worker; a preview never outlives the translation call.</summary>
internal sealed class PreviewPump(Action<SegmentProgress> render)
{
    private readonly object _gate = new();
    private SegmentProgress? _pending;
    private Task _worker = Task.CompletedTask;
    private bool _running, _stopped;

    public void Post(SegmentProgress snapshot)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _pending = snapshot;
            if (_running) return;
            _running = true;
            _worker = Task.Run(Drain);
        }
    }

    public Task StopAsync()
    {
        lock (_gate) { _stopped = true; _pending = null; return _worker; }
    }

    private void Drain()
    {
        while (true)
        {
            SegmentProgress snapshot;
            lock (_gate)
            {
                if (_pending is null) { _running = false; return; }
                snapshot = _pending; _pending = null;
            }
            // A preview is best effort: the final render reports any real failure.
            try { render(snapshot); }
            catch (Exception) { }
        }
    }
}

public static class ImageComposer
{
    public static SKBitmap Copy(SKBitmap source) => source.Copy(SKColorType.Bgra8888) ?? throw new InvalidOperationException("无法创建截图副本。");
    public static byte[] EncodePng(SKBitmap image)
    {
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded?.ToArray() ?? throw new InvalidOperationException("PNG 编码失败。");
    }
}
