using System.Text;
using System.Text.Json.Nodes;

namespace SnapTrans.Core.Llm;

public interface ILlmClient
{
    IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, CancellationToken ct = default);
}

public sealed record LlmRequest(string Model, string? SystemPrompt, IReadOnlyList<LlmMessage> Messages,
    int MaxOutputTokens = 4096, double? Temperature = null, JsonSchemaSpec? OutputSchema = null,
    JsonObject? ExtraBody = null);
public sealed record JsonSchemaSpec(string Name, JsonObject Schema, string? Mode = null);
public enum LlmRole { User, Assistant }
public sealed record LlmMessage(LlmRole Role, IReadOnlyList<LlmPart> Parts);
public abstract record LlmPart;
public sealed record TextPart(string Text) : LlmPart;
public sealed record ImagePart(byte[] Data, string MediaType) : LlmPart;
public abstract record LlmDelta;
public sealed record TextDelta(string Text) : LlmDelta;
public sealed record ReasoningDelta(string Text) : LlmDelta;
public sealed record UsageDelta(int? InputTokens, int? OutputTokens) : LlmDelta;
public sealed record DoneDelta(string? StopReason) : LlmDelta;
public sealed record LlmRequestMetadata(string Method, string Host, int? StatusCode, double ElapsedMilliseconds, int Attempt);

/// <summary>Contains only a safe, user-facing description, never the response body.</summary>
public sealed class LlmException(string message, int? statusCode = null, bool visionUnsupported = false)
    : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
    public bool VisionUnsupported { get; } = visionUnsupported;
}

public static class LlmClientExtensions
{
    public static async Task<string> CompleteAsync(this ILlmClient client, LlmRequest request, CancellationToken ct = default)
    {
        var text = new StringBuilder();
        await foreach (var delta in client.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
            if (delta is TextDelta part) text.Append(part.Text);
        return text.ToString();
    }
}
