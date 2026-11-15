using SkiaSharp;
using SnapTrans.Core.Llm;
using SnapTrans.Core.Settings;

namespace SnapTrans.Core.Translation;

public sealed record ConnectionTestResult(bool TextAvailable, bool? VisionAvailable, string TextMessage, string? VisionMessage);

public static class ConnectionTester
{
    public static async Task<ConnectionTestResult> TestAsync(ILlmClient client, Profile profile, bool testVision,
        CancellationToken ct = default)
    {
        var (textOk, textMessage) = await ProbeAsync(client, profile,
            [new TextPart("Reply with the word OK.")], ct).ConfigureAwait(false);
        if (!testVision) return new ConnectionTestResult(textOk, null, textMessage, "未测试图像能力");
        using var bitmap = new SKBitmap(32, 32, SKColorType.Bgra8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.Red);
        var (visionOk, visionMessage) = await ProbeAsync(client, profile,
            [ImageEncoder.Encode(bitmap), new TextPart("Name the main color of this image in one word.")], ct).ConfigureAwait(false);
        return new ConnectionTestResult(textOk, visionOk, textMessage, visionMessage);
    }

    private static async Task<(bool, string)> ProbeAsync(ILlmClient client, Profile profile, IReadOnlyList<LlmPart> parts,
        CancellationToken ct)
    {
        try
        {
            var text = await client.CompleteAsync(new LlmRequest(profile.Model, null,
                [new LlmMessage(LlmRole.User, parts)], 64), ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? (false, "服务未返回可见文字") : (true, "连接成功");
        }
        catch (LlmException ex) { return (false, ex.Message); }
    }
}
