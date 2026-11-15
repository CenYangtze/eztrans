using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnapTrans.Core.Llm;
using SnapTrans.Core.Settings;

namespace SnapTrans.Core.ImageEditing;

public sealed class OpenAiImageEditClient(HttpClient http, Profile profile, string apiKey = "") : HttpImageEditClient(http, profile, apiKey)
{
    public override HttpRequestMessage BuildRequest(ImageEditRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, Endpoint(Profile.BaseUrl, "/images/edits"));
        if (ApiKey.Length > 0) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(Profile.Model), "model");
        form.Add(new StringContent(request.Prompt), "prompt");
        form.Add(new StringContent("1"), "n");
        var image = new ByteArrayContent(request.Image);
        image.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(image, "image", request.MediaType == "image/jpeg" ? "screenshot.jpg" : request.MediaType == "image/webp" ? "screenshot.webp" : "screenshot.png");
        // GPT image models return base64 by default and reject the retired response_format parameter.
        foreach (var (key, value) in Profile.ExtraBody)
        {
            if (value is null || key is "model" or "prompt" or "image" or "n" or "response_format") continue;
            string text = value is JsonValue scalar && scalar.TryGetValue<string>(out var str) ? str : value.ToJsonString();
            form.Add(new StringContent(text), key);
        }
        message.Content = form;
        return message;
    }

    protected override ImageEditResponse ParseResponse(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new LlmException("AI 服务未返回图片，请确认所选模型支持图像编辑。");
        var image = data[0];
        if (image.ValueKind == JsonValueKind.Object && image.TryGetProperty("b64_json", out var bytes) && bytes.ValueKind == JsonValueKind.String)
            return DecodeImage(bytes.GetString());
        throw new LlmException("图像服务未返回 base64 图片；请使用支持 b64_json 的图像模型，外部图片链接不会自动下载。");
    }
}
