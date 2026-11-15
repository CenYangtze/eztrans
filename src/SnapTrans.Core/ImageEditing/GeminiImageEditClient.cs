using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnapTrans.Core.Llm;
using SnapTrans.Core.Settings;

namespace SnapTrans.Core.ImageEditing;

public sealed class GeminiImageEditClient(HttpClient http, Profile profile, string apiKey = "") : HttpImageEditClient(http, profile, apiKey)
{
    public JsonObject BuildBody(ImageEditRequest request)
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user", ["parts"] = new JsonArray(
                    new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = request.MediaType, ["data"] = Convert.ToBase64String(request.Image) } },
                    new JsonObject { ["text"] = request.Prompt })
            }),
            ["generationConfig"] = new JsonObject { ["responseModalities"] = new JsonArray("TEXT", "IMAGE") }
        };
        Merge(body, Profile.ExtraBody);
        return body;
    }
    public override HttpRequestMessage BuildRequest(ImageEditRequest request)
    {
        // Reuse the documented Gemini compatibility preset, switching only its known /openai suffix.
        string baseUrl = Profile.BaseUrl.TrimEnd('/');
        if (baseUrl.EndsWith("/openai", StringComparison.OrdinalIgnoreCase)) baseUrl = baseUrl[..^7];
        string model = Profile.Model.StartsWith("models/", StringComparison.Ordinal) ? Profile.Model[7..] : Profile.Model;
        var endpoint = Endpoint(baseUrl, "/models/" + Uri.EscapeDataString(model) + ":generateContent");
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (ApiKey.Length > 0) message.Headers.TryAddWithoutValidation("x-goog-api-key", ApiKey);
        message.Content = new StringContent(BuildBody(request).ToJsonString(), Encoding.UTF8, "application/json");
        return message;
    }
    protected override ImageEditResponse ParseResponse(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
            foreach (var candidate in candidates.EnumerateArray())
                if (candidate.ValueKind == JsonValueKind.Object && candidate.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object &&
                    content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    foreach (var part in parts.EnumerateArray())
                        if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("inlineData", out var image) && image.ValueKind == JsonValueKind.Object &&
                            image.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.String)
                            return DecodeImage(value.GetString(), image.TryGetProperty("mimeType", out var media) && media.ValueKind == JsonValueKind.String ? media.GetString() : null);
        throw new LlmException("Gemini 未返回图像；请确认所选模型支持图像输出，且请求未被服务拒绝。");
    }
    private static void Merge(JsonObject target, JsonObject extra)
    {
        foreach (var (key, value) in extra)
        {
            if (key == "contents") continue;
            if (target[key] is JsonObject child && value is JsonObject patch) Merge(child, patch);
            else target[key] = value?.DeepClone();
        }
    }
}
