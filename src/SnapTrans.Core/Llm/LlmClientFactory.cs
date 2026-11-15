using SnapTrans.Core.Settings;

namespace SnapTrans.Core.Llm;

public static class LlmClientFactory
{
    public static ILlmClient Create(HttpClient httpClient, Profile profile, string apiKey = "",
        Action<LlmRequestMetadata>? observeRequest = null)
    {
        HttpLlmClient client = profile.Kind == "anthropic"
            ? new AnthropicClient(httpClient, profile, apiKey)
            : new OpenAiCompatibleClient(httpClient, profile, apiKey);
        client.ObserveRequest = observeRequest;
        return client;
    }
}
