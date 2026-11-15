using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;
using SnapTrans.Core.Layout;
using SnapTrans.Core.Llm;
using SnapTrans.Core.Settings;

namespace SnapTrans.Core.Translation;

public sealed partial class SegmentTranslator(ILlmClient client) : IProgressiveSegmentTranslator
{
    private const string SchemaText = """
        {"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"id":{"type":"integer"},"text":{"type":"string"}},"required":["id","text"],"additionalProperties":false}}},"required":["items"],"additionalProperties":false}
        """;
    /// <summary>The image only disambiguates text that OCR already supplied, so a reduced copy is enough.</summary>
    public const int ContextImageLongEdge = 1280;
    [GeneratedRegex(@"<think>.*?(?:</think>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkRegex();

    /// <summary>One distinct source text; identical blocks share a single request item.</summary>
    private sealed record Unit(int Id, string Text, int MaxChars, IReadOnlyList<int> BlockIds);

    public Task<SegmentResult> TranslateAsync(IReadOnlyList<TextBlock> blocks, SKBitmap? contextImage,
        TranslateOptions options, CancellationToken ct = default) => TranslateAsync(blocks, contextImage, options, null, ct);

    public async Task<SegmentResult> TranslateAsync(IReadOnlyList<TextBlock> blocks, SKBitmap? contextImage,
        TranslateOptions options, Action<SegmentProgress>? progress, CancellationToken ct = default)
    {
        if (blocks.Select(b => b.Id).Distinct().Count() != blocks.Count) throw new ArgumentException("文本块 ID 必须唯一", nameof(blocks));
        if (blocks.Count == 0) return new SegmentResult(new Dictionary<int, string>(), []);
        var translations = new Dictionary<int, string>();
        // Numbers, punctuation and symbols need no model call; an identical "translation" keeps the pixels untouched.
        foreach (var block in blocks.Where(b => !NeedsTranslation(b.SourceText))) translations[block.Id] = block.SourceText;
        var units = Units(blocks.Where(b => NeedsTranslation(b.SourceText)).ToArray(), ScriptDetector.IsCjkLanguage(options.TargetLanguage));
        if (units.Count == 0) return new SegmentResult(translations, []);

        var allContext = string.Join('\n', units.Select(u => $"{u.Id}: {u.Text}"));
        if (allContext.Length > 2000) allContext = allContext[..2000];
        var image = contextImage is not null && options.Profile.SupportsVision ? ImageEncoder.Encode(contextImage, ContextImageLongEdge) : null;
        int concurrency = ProfileRules.Concurrency(options.Profile);
        var (maxUnits, maxChars) = BatchLimits(units.Sum(u => u.Text.Length), concurrency);
        var batches = Batch(units, u => u.Text.Length, maxUnits, maxChars);
        using var limit = new SemaphoreSlim(concurrency);
        var gate = new object();
        int completed = 0;
        LlmException? failure = null;
        var tasks = batches.Select(async batch =>
        {
            await limit.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                SegmentResult result;
                try { result = await TranslateBatchAsync(batch, image, allContext, options, ct).ConfigureAwait(false); }
                catch (LlmException error)
                {
                    // One failed batch keeps its source text; the remaining batches still render.
                    lock (gate) { failure ??= error; completed++; }
                    return;
                }
                SegmentProgress? snapshot;
                lock (gate)
                {
                    foreach (var unit in batch)
                        if (result.Translations.TryGetValue(unit.Id, out var text))
                            foreach (int id in unit.BlockIds) translations[id] = text;
                    completed++;
                    snapshot = progress is null ? null : new(new Dictionary<int, string>(translations), completed, batches.Count);
                }
                if (snapshot is not null) progress!(snapshot);
            }
            finally { limit.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var translated = units.SelectMany(u => u.BlockIds).Count(translations.ContainsKey);
        if (failure is not null && translated == 0) throw failure;
        return new SegmentResult(translations, blocks.Where(b => !translations.ContainsKey(b.Id)).Select(b => b.Id).ToArray());
    }

    /// <summary>Text without a single letter (numbers, prices, symbols) is the same in every language.</summary>
    public static bool NeedsTranslation(string text)
    {
        foreach (var rune in text.EnumerateRunes()) if (Rune.IsLetter(rune)) return true;
        return false;
    }

    /// <summary>
    /// Sequential servers get the original large batches. Parallel services get batches sized so that
    /// about <paramref name="concurrency"/> requests share the work, which shortens the wait for long pages.
    /// </summary>
    public static (int MaxBlocks, int MaxChars) BatchLimits(int totalChars, int concurrency) =>
        concurrency <= 1 ? (60, 6000) : (40, Math.Clamp(totalChars / concurrency + 1, 300, 1500));

    public static IReadOnlyList<IReadOnlyList<TextBlock>> Batch(IReadOnlyList<TextBlock> blocks, int maxBlocks = 60, int maxChars = 6000) =>
        Batch(blocks, b => b.SourceText.Length, maxBlocks, maxChars);

    private static IReadOnlyList<IReadOnlyList<T>> Batch<T>(IReadOnlyList<T> items, Func<T, int> length, int maxItems, int maxChars)
    {
        var batches = new List<IReadOnlyList<T>>();
        var batch = new List<T>();
        var count = 0;
        foreach (var item in items)
        {
            if (batch.Count > 0 && (batch.Count >= maxItems || count + length(item) > maxChars))
            { batches.Add(batch); batch = []; count = 0; }
            batch.Add(item); count += length(item);
        }
        if (batch.Count > 0) batches.Add(batch);
        return batches;
    }

    private static IReadOnlyList<Unit> Units(IReadOnlyList<TextBlock> blocks, bool cjk)
    {
        var units = new List<Unit>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            int maxChars = Math.Max(1, (int)Math.Floor(1.2 * Math.Max(1, block.Lines.Count) * block.Bounds.Width /
                ((cjk ? 1 : .55) * Math.Max(1, block.LineHeight))));
            string key = string.Join(' ', block.SourceText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (index.TryGetValue(key, out int existing))
            {
                var unit = units[existing];
                units[existing] = unit with { MaxChars = Math.Min(unit.MaxChars, maxChars), BlockIds = [.. unit.BlockIds, block.Id] };
            }
            else { index[key] = units.Count; units.Add(new(block.Id, block.SourceText, maxChars, [block.Id])); }
        }
        return units;
    }

    private async Task<SegmentResult> TranslateBatchAsync(IReadOnlyList<Unit> batch, ImagePart? image,
        string context, TranslateOptions options, CancellationToken ct)
    {
        var mode = options.Profile.StructuredOutput;
        string output;
        var downgraded = false;
        try { output = await RequestAsync(batch, image, context, options, mode, ct).ConfigureAwait(false); }
        catch (LlmException ex) when (ex.StatusCode == 400 && !ex.VisionUnsupported && mode != "none")
        {
            mode = mode == "json_schema" ? "json_object" : "none";
            downgraded = true;
            output = await RequestAsync(batch, image, context, options, mode, ct).ConfigureAwait(false);
        }
        SegmentResult parsed;
        try { parsed = Parse(output, batch.Select(b => b.Id)); }
        catch (JsonException)
        {
            if (downgraded) throw new LlmException("模型未返回有效的分段译文，请更换模型或关闭结构化输出后重试");
            mode = mode switch { "json_schema" => "json_object", _ => "none" };
            output = await RequestAsync(batch, image, context, options, mode, ct).ConfigureAwait(false);
            try { parsed = Parse(output, batch.Select(b => b.Id)); }
            catch (JsonException) { throw new LlmException("模型未返回有效的分段译文，请更换模型或关闭结构化输出后重试"); }
        }
        if (parsed.MissingIds.Count == 0) return parsed;
        var missing = batch.Where(b => parsed.MissingIds.Contains(b.Id)).ToArray();
        var retryContext = context + "\nAlready translated (context only):\n" +
            JsonSerializer.Serialize(parsed.Translations);
        var retry = await RequestAsync(missing, image, retryContext, options, mode, ct).ConfigureAwait(false);
        SegmentResult repeated;
        try { repeated = Parse(retry, missing.Select(b => b.Id)); }
        catch (JsonException) { return parsed; } // Preserve source rather than erasing an untranslated block.
        var combined = parsed.Translations.Concat(repeated.Translations).ToDictionary(k => k.Key, v => v.Value);
        return new SegmentResult(combined, batch.Where(b => !combined.ContainsKey(b.Id)).Select(b => b.Id).ToArray());
    }

    private Task<string> RequestAsync(IReadOnlyList<Unit> batch, ImagePart? image, string context,
        TranslateOptions options, string mode, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            target_lang = PromptBuilder.LanguageName(options.TargetLanguage),
            context,
            segments = batch.Select(b => new { id = b.Id, text = b.Text, max_chars = b.MaxChars })
        });
        var parts = new List<LlmPart>();
        if (image is not null) parts.Add(image);
        parts.Add(new TextPart(payload));
        return client.CompleteAsync(new LlmRequest(options.Profile.Model,
            PromptBuilder.Build(options.Prompts?.Segments ?? PromptTemplates.Segments, options),
            [new LlmMessage(LlmRole.User, parts)], options.Profile.MaxOutputTokens, options.Profile.Temperature,
            new JsonSchemaSpec("translations", JsonNode.Parse(SchemaText)!.AsObject(), mode)), ct);
    }

    public static SegmentResult Parse(string response, IEnumerable<int> expectedIds)
    {
        var clean = ThinkRegex().Replace(response, "");
        var start = clean.IndexOf('{');
        var end = clean.LastIndexOf('}');
        if (start < 0 || end < start) throw new JsonException("Missing JSON object");
        using var document = JsonDocument.Parse(clean[start..(end + 1)]);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new JsonException("Missing items array");
        var expected = expectedIds.ToHashSet();
        var counts = new Dictionary<int, int>();
        var translated = new Dictionary<int, string>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idValue) ||
                idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetInt32(out var id) || !expected.Contains(id)) continue;
            counts[id] = counts.GetValueOrDefault(id) + 1;
            if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
                text.GetString() is { } value && !string.IsNullOrWhiteSpace(value)) translated[id] = value;
        }
        foreach (var id in counts.Where(k => k.Value != 1).Select(k => k.Key)) translated.Remove(id);
        return new SegmentResult(translated, expected.Where(id => !translated.ContainsKey(id)).Order().ToArray());
    }
}
