using System.Text.Json;
using System.Text.Json.Nodes;

namespace SnapTrans.Core.Dictionaries;

/// <summary>Looks a word up in the selected source, or in each source in turn, and remembers recent answers.</summary>
public sealed class DictionaryService(IReadOnlyList<IDictionaryProvider> providers)
{
    private const int CacheLimit = 128;
    private readonly Dictionary<string, DictionaryEntry> _cache = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    /// <exception cref="DictionaryException">Nothing was found, or no source could be reached.</exception>
    public async Task<DictionaryEntry> LookupAsync(string? text, string source, CancellationToken ct)
    {
        string query = WordPicker.Normalize(text) ?? throw new DictionaryException("没有可查询的单词。");
        string key = source + "\n" + query;
        lock (_sync) if (_cache.TryGetValue(key, out var cached)) return cached;
        // OCR keeps a sentence-initial capital; dictionaries index the lower-case headword.
        string[] spellings = query.Any(char.IsUpper) ? [query, query.ToLowerInvariant()] : [query];
        bool answered = false;
        Exception? failure = null;
        foreach (var provider in providers.Where(p => source == DictionarySources.Auto || p.Id == source))
        {
            try
            {
                foreach (string spelling in spellings)
                {
                    var entry = await provider.LookupAsync(spelling, ct).ConfigureAwait(false);
                    answered = true;
                    if (entry is null) continue;
                    lock (_sync) { if (_cache.Count >= CacheLimit) _cache.Clear(); _cache[key] = entry; }
                    return entry;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or JsonException or IOException or InvalidOperationException or OperationCanceledException) { failure = e; }
        }
        throw answered
            ? new DictionaryException($"没有查到“{query}”的释义。")
            : new DictionaryException("无法连接词典服务，请检查网络或代理。", failure);
    }
}

internal static class DictionaryHttp
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private const int MaxBytes = 2 * 1024 * 1024;

    public static async Task<string> GetStringAsync(Func<HttpClient> createClient, Uri uri, CancellationToken ct)
    {
        using var http = createClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // Both services answer browsers only; no identifying detail is sent.
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes) throw new HttpRequestException("Dictionary response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes) throw new HttpRequestException("Dictionary response is too large.");
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}

/// <summary>Reads loosely shaped service JSON without throwing when a field has another type.</summary>
internal static class Json
{
    public static JsonNode? Get(JsonNode? node, params string[] path)
    {
        foreach (string name in path)
        {
            if (node is not JsonObject item || !item.TryGetPropertyValue(name, out node)) return null;
        }
        return node;
    }

    /// <summary>The elements of an array, a lone object or value as one item, nothing for null.</summary>
    public static IEnumerable<JsonNode?> Items(JsonNode? node) => node switch { null => Array.Empty<JsonNode?>(), JsonArray array => array, _ => new[] { node } };

    public static string? Text(JsonNode? node)
    {
        if (node is JsonArray { Count: > 0 } array) node = array[0];
        return node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    }
}
