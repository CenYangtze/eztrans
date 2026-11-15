using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnapTrans.Core.Settings;

namespace SnapTrans.Core.Llm;

public sealed class OpenAiCompatibleClient(HttpClient http, Profile profile, string apiKey = "")
    : HttpLlmClient(http, profile, apiKey)
{
    protected override string Path => "/chat/completions";
    protected override void SetHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }
    public override JsonObject BuildBody(LlmRequest request)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt });
        foreach (var message in request.Messages)
        {
            var parts = new JsonArray();
            foreach (var part in message.Parts.OrderBy(x => x is ImagePart ? 0 : 1))
            {
                if (part is TextPart text) parts.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                if (part is ImagePart image)
                {
                    var imageUrl = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}" };
                    if (!string.IsNullOrEmpty(Profile.ImageDetail)) imageUrl["detail"] = Profile.ImageDetail;
                    parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = imageUrl });
                }
            }
            messages.Add(new JsonObject { ["role"] = message.Role == LlmRole.User ? "user" : "assistant", ["content"] = parts });
        }
        var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages, ["stream"] = true };
        body[Profile.MaxTokensField == "max_completion_tokens" ? "max_completion_tokens" : "max_tokens"] = request.MaxOutputTokens;
        if (request.Temperature is { } temp) body["temperature"] = temp;
        if (request.OutputSchema is { } schema)
        {
            var mode = schema.Mode ?? Profile.StructuredOutput;
            if (mode == "json_schema") body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema", ["json_schema"] = new JsonObject
                { ["name"] = schema.Name, ["strict"] = true, ["schema"] = schema.Schema.DeepClone() }
            };
            else if (mode == "json_object") body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }
        DeepMerge(body, Profile.ExtraBody);
        DeepMerge(body, request.ExtraBody);
        body["stream"] = true; // Response parsing always expects SSE, even with custom parameters.
        return body;
    }
    protected override IEnumerable<LlmDelta> ParseEvent(SseEvent item)
    {
        if (item.Data.Trim() == "[DONE]") { yield return new DoneDelta(null); yield break; }
        using var json = JsonDocument.Parse(item.Data);
        var root = json.RootElement;
        if (root.TryGetProperty("error", out _)) throw new LlmException("模型在输出过程中返回错误，请检查配置后重试");
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            yield return new UsageDelta(Int(usage, "prompt_tokens"), Int(usage, "completion_tokens"));
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) yield break;
        var choice = choices[0];
        if (choice.TryGetProperty("delta", out var delta))
        {
            if (String(delta, "reasoning_content") is { Length: > 0 } reasoning) yield return new ReasoningDelta(reasoning);
            if (String(delta, "content") is { Length: > 0 } text) yield return new TextDelta(text);
        }
        if (String(choice, "finish_reason") is { } reason) yield return new DoneDelta(reason);
    }
}
