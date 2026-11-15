using System.Net;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnapTrans.Core.Settings;

namespace SnapTrans.Core.Llm;

public abstract class HttpLlmClient(HttpClient http, Profile profile, string apiKey) : ILlmClient
{
    protected Profile Profile { get; } = profile;
    protected string ApiKey { get; } = apiKey;
    /// <summary>Opt-in, strictly metadata-only diagnostics; never receives a URI path, key, prompt or image.</summary>
    public Action<LlmRequestMetadata>? ObserveRequest { get; set; }
    protected abstract string Path { get; }
    public abstract JsonObject BuildBody(LlmRequest request);
    protected abstract IEnumerable<LlmDelta> ParseEvent(SseEvent item);
    protected abstract void SetHeaders(HttpRequestMessage request);

    public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Profile.TimeoutSeconds)));
        using var response = await SendWithRetrySafeAsync(request, overall.Token, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(overall.Token).ConfigureAwait(false);
        await using var events = SseReader.ReadAsync(stream, overall.Token).GetAsyncEnumerator(overall.Token);
        var filter = new ThinkFilter();
        var completed = false;
        while (await MoveNextAsync(events, overall.Token, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(events.Current.Data)) continue;
            IEnumerable<LlmDelta> parsed;
            try { parsed = ParseEvent(events.Current).ToArray(); }
            catch (JsonException) { throw new LlmException("服务返回的流式数据格式无效"); }
            catch (InvalidOperationException) { throw new LlmException("服务返回的流式数据结构无效"); }
            foreach (var delta in parsed)
            {
                if (delta is TextDelta text)
                {
                    var clean = filter.Push(text.Text);
                    if (clean.Length > 0) yield return new TextDelta(clean);
                }
                else
                {
                    if (delta is DoneDelta)
                    {
                        if (completed) continue;
                        completed = true;
                    }
                    yield return delta;
                }
            }
            if (events.Current.Data.Trim() == "[DONE]" || events.Current.Event == "message_stop") break;
        }
        var tail = filter.Finish();
        if (tail.Length > 0) yield return new TextDelta(tail);
        if (!completed) throw new LlmException("服务响应中途断开，请重试");
    }

    private async Task<HttpResponseMessage> SendWithRetrySafeAsync(LlmRequest request, CancellationToken overall,
        CancellationToken caller)
    {
        try { return await SendWithRetryAsync(request, overall, caller).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        { throw new LlmException("请求超时，请检查网络或增大模型超时设置"); }
    }

    private static async Task<bool> MoveNextAsync(IAsyncEnumerator<SseEvent> events, CancellationToken overall,
        CancellationToken caller)
    {
        try { return await events.MoveNextAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) when (overall.IsCancellationRequested && !caller.IsCancellationRequested)
        { throw new LlmException("请求超时，请检查网络或增大模型超时设置"); }
        catch (IOException) { throw new LlmException("服务响应中途断开，请重试"); }
        catch (HttpRequestException) { throw new LlmException("网络连接中断，请检查网络后重试"); }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(LlmRequest request, CancellationToken overall,
        CancellationToken caller)
    {
        var body = BuildBody(request).ToJsonString();
        for (var attempt = 0; ; attempt++)
        {
            overall.ThrowIfCancellationRequested();
            using var message = new HttpRequestMessage(HttpMethod.Post, GetEndpoint(Profile.BaseUrl, Path));
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            SetHeaders(message);
            HttpResponseMessage response;
            var watch = Stopwatch.StartNew();
            try
            {
                response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, overall).ConfigureAwait(false);
                Observe(message, (int)response.StatusCode, watch.Elapsed.TotalMilliseconds, attempt);
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                Observe(message, null, watch.Elapsed.TotalMilliseconds, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(attempt == 0 ? 500 : 1500), overall).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException)
            {
                Observe(message, null, watch.Elapsed.TotalMilliseconds, attempt);
                throw new LlmException("网络连接失败，请检查接口地址与代理设置");
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            { throw new LlmException("请求超时，请检查网络或增大模型超时设置"); }
            if (response.IsSuccessStatusCode) return response;
            var retry = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (retry && attempt < 2)
            {
                var delay = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? response.Headers.RetryAfter?.Delta ??
                      (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(attempt == 0 ? 1 : 3))
                    : TimeSpan.FromMilliseconds(attempt == 0 ? 500 : 1500);
                response.Dispose();
                await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, overall).ConfigureAwait(false);
                continue;
            }
            try { throw await CreateErrorAsync(response, overall).ConfigureAwait(false); }
            finally { response.Dispose(); }
        }
    }

    private void Observe(HttpRequestMessage request, int? status, double elapsed, int attempt)
    {
        // Diagnostics must never turn a successful transport into a failed request.
        try { ObserveRequest?.Invoke(new LlmRequestMetadata("POST", request.RequestUri!.Host, status, elapsed, attempt + 1)); }
        catch { }
    }

    private async Task<LlmException> CreateErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var code = (int)response.StatusCode;
        string? detail = null;
        // Read only a bounded error body, do not retain it in any exception or log.
        await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            var bytes = new byte[16384];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), ct).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            try
            {
                using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String)
                    detail = text.GetString();
            }
            catch (JsonException) { }
        }
        var vision = code == 400 && detail is not null &&
            (detail.Contains("image", StringComparison.OrdinalIgnoreCase) || detail.Contains("vision", StringComparison.OrdinalIgnoreCase));
        var message = code switch
        {
            401 or 403 => "API Key 无效或无权限，请检查模型配置",
            404 => "接口地址或模型名错误，请检查模型配置",
            429 => "请求过于频繁，请稍后重试",
            >= 500 => "模型服务暂时不可用，请稍后重试",
            _ when vision => "当前模型不支持图片，请改用 OCR+Text 管线",
            _ => "模型服务拒绝请求，请检查模型参数"
        };
        // Provider messages may echo prompts/credentials. Only display short messages that
        // cannot contain request data; the common status classes above have stable wording.
        if (code == 400 && !vision && detail is { Length: > 0 and < 200 } &&
            !detail.Contains("data:", StringComparison.OrdinalIgnoreCase) && !detail.Contains('{') &&
            !detail.Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            if (ApiKey.Length > 0) detail = detail.Replace(ApiKey, "[已隐藏]", StringComparison.Ordinal);
            message += "：" + detail;
        }
        return new LlmException(message, code, vision);
    }

    public static Uri GetEndpoint(string baseUrl, string path)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new LlmException("模型接口地址必须是有效的 HTTP/HTTPS 地址");
        var prefix = uri.AbsoluteUri.TrimEnd('/');
        if (path == "/v1/messages" && prefix.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path = "/messages";
        return new Uri(prefix + path);
    }

    protected static void DeepMerge(JsonObject target, JsonObject? extra)
    {
        if (extra is null) return;
        foreach (var (key, value) in extra)
        {
            if (target[key] is JsonObject child && value is JsonObject patch) DeepMerge(child, patch);
            else target[key] = value?.DeepClone();
        }
    }
    protected static int? Int(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    protected static string? String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
