using SnapTrans.Core.Llm;
using SnapTrans.Core.Settings;

namespace SnapTrans.Core.ImageEditing;

public sealed record ImageEditRequest(byte[] Image, string MediaType, string Prompt);
public sealed record ImageEditResponse(byte[] Image, string MediaType);
public interface IImageEditClient
{
    Task<ImageEditResponse> EditAsync(ImageEditRequest request, CancellationToken ct = default);
}

public static class ImageEditClientFactory
{
    public static IImageEditClient Create(string provider, HttpClient http, Profile profile, string apiKey = "",
        Action<LlmRequestMetadata>? observeRequest = null)
    {
        HttpImageEditClient client = provider switch
        {
            "openai" => new OpenAiImageEditClient(http, profile, apiKey),
            "gemini" => new GeminiImageEditClient(http, profile, apiKey),
            _ => throw new ArgumentException("未知的 AI 图像编辑接口类型。", nameof(provider))
        };
        client.ObserveRequest = observeRequest;
        return client;
    }
}
