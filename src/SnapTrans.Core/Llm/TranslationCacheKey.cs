using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnapTrans.Core.Settings;

namespace SnapTrans.Core.Llm;

/// <summary>
/// A versioned, opaque key covering every configurable input that can affect a translation.
/// Images contribute only their digest. The key never contains source text or credentials.
/// </summary>
public static class TranslationCacheKey
{
    public static string Create(Profile profile, LlmRequest request)
    {
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            var parts = new JsonArray();
            foreach (var part in message.Parts.OrderBy(p => p is ImagePart ? 0 : 1))
                parts.Add(part switch
                {
                    TextPart text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
                    ImagePart image => new JsonObject
                    {
                        ["type"] = "image", ["mediaType"] = image.MediaType,
                        ["length"] = image.Data.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(image.Data))
                    },
                    _ => throw new ArgumentException("不支持的模型消息类型", nameof(request))
                });
            messages.Add(new JsonObject { ["role"] = message.Role.ToString(), ["parts"] = parts });
        }
        var data = new JsonObject
        {
            ["version"] = "snaptrans.llm.cache.v1",
            ["profileId"] = profile.Id,
            ["baseUrl"] = NormalizeBaseUrl(profile.BaseUrl),
            ["kind"] = profile.Kind,
            // A newly entered DPAPI-protected credential invalidates prior results for that profile.
            ["credentialScope"] = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(profile.ApiKeyProtected ?? ""))),
            ["model"] = request.Model,
            ["system"] = request.SystemPrompt,
            ["messages"] = messages,
            ["maxOutputTokens"] = request.MaxOutputTokens,
            ["temperature"] = profile.Kind == "anthropic" ? null : request.Temperature,
            ["maxTokensField"] = profile.MaxTokensField,
            ["imageDetail"] = profile.ImageDetail,
            ["authHeader"] = profile.AuthHeader,
            ["anthropicVersion"] = profile.AnthropicVersion,
            ["structuredOutput"] = request.OutputSchema?.Mode ?? profile.StructuredOutput,
            ["schemaName"] = request.OutputSchema?.Name,
            ["schema"] = request.OutputSchema?.Schema.DeepClone(),
            ["profileExtra"] = profile.ExtraBody.DeepClone(),
            ["requestExtra"] = request.ExtraBody?.DeepClone()
        };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, data);
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static string NormalizeBaseUrl(string value)
    {
        var trimmed = value.TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri.AbsoluteUri.TrimEnd('/') : trimmed;
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null: writer.WriteNullValue(); break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(pair.Key);
                    WriteCanonical(writer, pair.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var child in array) WriteCanonical(writer, child);
                writer.WriteEndArray();
                break;
            default: node.WriteTo(writer); break;
        }
    }
}
