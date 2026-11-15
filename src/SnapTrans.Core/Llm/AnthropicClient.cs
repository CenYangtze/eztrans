using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnapTrans.Core.Settings;

namespace SnapTrans.Core.Llm;

public sealed class AnthropicClient(HttpClient http, Profile profile, string apiKey = "")
    : HttpLlmClient(http, profile, apiKey)
{
    protected override string Path => "/v1/messages";
    protected override void SetHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("anthropic-version", Profile.AnthropicVersion);
        if (string.IsNullOrWhiteSpace(ApiKey)) return;
        if (Profile.AuthHeader == "x-api-key") request.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }
    public override JsonObject BuildBody(LlmRequest request)
    {
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            var parts = new JsonArray();
            foreach (var part in message.Parts.OrderBy(x => x is ImagePart ? 0 : 1))
            {
                if (part is TextPart text) parts.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                if (part is ImagePart image) parts.Add(new JsonObject
                {
                    ["type"] = "image", ["source"] = new JsonObject
                    { ["type"] = "base64", ["media_type"] = image.MediaType, ["data"] = Convert.ToBase64String(image.Data) }
                });
            }
            messages.Add(new JsonObject { ["role"] = message.Role == LlmRole.User ? "user" : "assistant", ["content"] = parts });
        }
        var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages, ["max_tokens"] = request.MaxOutputTokens, ["stream"] = true };
        if (!string.IsNullOrEmpty(request.SystemPrompt)) body["system"] = request.SystemPrompt;
        // Native Messages has no json_object mode. That downgrade relies on the JSON
        // prompt instead of re-sending the same schema that the server just rejected.
        if (request.OutputSchema is { } schema && (schema.Mode ?? Profile.StructuredOutput) == "json_schema")
            body["output_config"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = schema.Schema.DeepClone() } };
        DeepMerge(body, Profile.ExtraBody);
        DeepMerge(body, request.ExtraBody);
        // These sampling fields are deliberately unavailable with the configured API version.
        body.Remove("temperature"); body.Remove("top_p"); body.Remove("top_k");
        body["stream"] = true;
        return body;
    }
    protected override IEnumerable<LlmDelta> ParseEvent(SseEvent item)
    {
        using var json = JsonDocument.Parse(item.Data);
        var root = json.RootElement;
        var type = String(root, "type") ?? item.Event;
        if (type == "error") throw new LlmException("Anthropic 在输出过程中返回错误，请稍后重试");
        if (type == "content_block_delta" && root.TryGetProperty("delta", out var delta))
        {
            if (String(delta, "type") == "text_delta" && String(delta, "text") is { } text) yield return new TextDelta(text);
            if (String(delta, "type") == "thinking_delta") yield return new ReasoningDelta("");
        }
        if (type == "message_start" && root.TryGetProperty("message", out var msg) && msg.TryGetProperty("usage", out var initialUsage))
            yield return new UsageDelta(Int(initialUsage, "input_tokens"), Int(initialUsage, "output_tokens"));
        if (type == "message_delta")
        {
            if (root.TryGetProperty("usage", out var usage)) yield return new UsageDelta(Int(usage, "input_tokens"), Int(usage, "output_tokens"));
            if (root.TryGetProperty("delta", out var messageDelta) && String(messageDelta, "stop_reason") is { } reason)
                yield return new DoneDelta(reason);
        }
        if (type == "message_stop") yield return new DoneDelta("stop");
    }
}
