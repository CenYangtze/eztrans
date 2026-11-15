using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SnapTrans.Core.Caching;
using SnapTrans.Core.Settings;

namespace SnapTrans.Core.Llm;

/// <summary>
/// Caches completed visible text only. It neither owns nor persists the shared cache.
/// Partial, failed, filtered, truncated and cancelled responses are never inserted.
/// </summary>
public sealed class CachedLlmClient(ILlmClient inner, Profile profile, ITranslationCache cache) : ILlmClient
{
    public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = TranslationCacheKey.Create(profile, request);
        var hit = await cache.GetAsync(key, ct).ConfigureAwait(false);
        if (hit is not null)
        {
            ct.ThrowIfCancellationRequested();
            yield return new CacheHitDelta();
            ct.ThrowIfCancellationRequested();
            yield return new TextDelta(hit);
            ct.ThrowIfCancellationRequested();
            yield return new DoneDelta("cache");
            yield break;
        }

        var text = new StringBuilder();
        var completed = false;
        var rejected = false;
        await foreach (var delta in inner.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (delta is TextDelta part) text.Append(part.Text);
            if (delta is DoneDelta done)
            {
                completed = true;
                rejected |= done.StopReason is "length" or "max_tokens" or "content_filter" or "refusal";
            }
            yield return delta;
        }
        ct.ThrowIfCancellationRequested();
        var result = text.ToString();
        if (completed && !rejected && !string.IsNullOrWhiteSpace(result) && IsCacheableResult(request, result))
            await cache.SetAsync(key, result, ct).ConfigureAwait(false);
    }

    private static bool IsCacheableResult(LlmRequest request, string result)
    {
        if (request.OutputSchema is null) return true;
        // Do not trap an invalid structured response in the cache: an explicit retry
        // must be able to reach the model again. Fences/prose accepted by the segment
        // parser remain acceptable when they contain a syntactically valid JSON object.
        var first = result.IndexOf('{');
        var last = result.LastIndexOf('}');
        if (first < 0 || last < first) return false;
        try
        {
            using var json = JsonDocument.Parse(result[first..(last + 1)]);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return false;
            return request.OutputSchema.Name != "translations" || HasEveryTranslation(request, json.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static bool HasEveryTranslation(LlmRequest request, JsonElement response)
    {
        var payload = request.Messages.LastOrDefault(m => m.Role == LlmRole.User)?.Parts.OfType<TextPart>().LastOrDefault()?.Text;
        if (payload is null) return false;
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array ||
            !response.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return false;
        var expected = new HashSet<int>();
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object || !segment.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || !expected.Add(number)) return false;
        }
        if (expected.Count == 0) return false;
        var translated = new HashSet<int>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number)) return false;
            if (!expected.Contains(number)) continue; // Extra ids are ignored by the segment translator as well.
            if (!translated.Add(number) || !item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(text.GetString())) return false;
        }
        return expected.SetEquals(translated);
    }
}

public sealed record CacheHitDelta : LlmDelta;
