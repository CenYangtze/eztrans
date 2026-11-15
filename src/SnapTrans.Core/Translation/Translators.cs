using System.Runtime.CompilerServices;
using SkiaSharp;
using SnapTrans.Core.Layout;
using SnapTrans.Core.Llm;

namespace SnapTrans.Core.Translation;

public sealed class VisionTranslator(ILlmClient client) : IVisionTranslator
{
    public async IAsyncEnumerable<LlmDelta> TranslateAsync(SKBitmap image, TranslateOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var template = options.Prompts?.Vision ?? PromptTemplates.Vision;
        var profile = options.Profile;
        var encoded = await Task.Run(() => ImageEncoder.Encode(image), ct).ConfigureAwait(false);
        var request = new LlmRequest(profile.Model, PromptBuilder.Build(template, options),
            [new LlmMessage(LlmRole.User, [encoded, new TextPart("Translate the text in this screenshot.")])],
            profile.MaxOutputTokens, profile.Temperature);
        await foreach (var delta in client.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false)) yield return delta;
    }
}

public sealed class TextTranslator(ILlmClient client) : ITextTranslator
{
    public async IAsyncEnumerable<LlmDelta> TranslateAsync(IReadOnlyList<TextBlock> blocks, TranslateOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var text = string.Join('\n', blocks.Select(b => b.SourceText));
        if (string.IsNullOrWhiteSpace(text)) { yield return new TextDelta("[NO_TEXT]"); yield return new DoneDelta("no_text"); yield break; }
        var target = ScriptDetector.SelectTarget(text, options.TargetLanguage, options.SecondaryLanguage, options.SmartSwap);
        // The deterministic script switch has already run; prevent the prompt switching it again.
        var effective = options with { TargetLanguage = target, SmartSwap = false };
        var profile = options.Profile;
        var request = new LlmRequest(profile.Model, PromptBuilder.Build(options.Prompts?.Text ?? PromptTemplates.Text, effective, text),
            [new LlmMessage(LlmRole.User, [new TextPart("<source>\n" + text + "\n</source>")])],
            profile.MaxOutputTokens, profile.Temperature);
        await foreach (var delta in client.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false)) yield return delta;
    }
}
