using SkiaSharp;
using SnapTrans.Core.Layout;
using SnapTrans.Core.Llm;
using SnapTrans.Core.Settings;

namespace SnapTrans.Core.Translation;

public sealed record GlossaryEntry(string Source, string Target);
public sealed record TranslateOptions(string TargetLanguage, string SecondaryLanguage, Profile Profile,
    IReadOnlyList<GlossaryEntry> Glossary, bool SmartSwap = true, PromptSettings? Prompts = null);
public sealed record SegmentResult(IReadOnlyDictionary<int, string> Translations, IReadOnlyList<int> MissingIds);
public interface IVisionTranslator
{
    IAsyncEnumerable<LlmDelta> TranslateAsync(SKBitmap image, TranslateOptions options, CancellationToken ct = default);
}
public interface ITextTranslator
{
    IAsyncEnumerable<LlmDelta> TranslateAsync(IReadOnlyList<TextBlock> blocks, TranslateOptions options, CancellationToken ct = default);
}
public interface ISegmentTranslator
{
    Task<SegmentResult> TranslateAsync(IReadOnlyList<TextBlock> blocks, SKBitmap? contextImage, TranslateOptions options,
        CancellationToken ct = default);
}
/// <summary>Translations finished so far, keyed by block id, after <see cref="CompletedBatches"/> of <see cref="TotalBatches"/> requests.</summary>
public sealed record SegmentProgress(IReadOnlyDictionary<int, string> Translations, int CompletedBatches, int TotalBatches);
public interface IProgressiveSegmentTranslator : ISegmentTranslator
{
    /// <summary><paramref name="progress"/> runs on a worker thread each time a batch finishes.</summary>
    Task<SegmentResult> TranslateAsync(IReadOnlyList<TextBlock> blocks, SKBitmap? contextImage, TranslateOptions options,
        Action<SegmentProgress>? progress, CancellationToken ct = default);
}
