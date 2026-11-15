using System.Diagnostics;
using System.Net;
using System.Text.Json;
using SnapTrans.Core.Llm;
using SnapTrans.Core.Settings;

namespace SnapTrans.Core.ImageEditing;

/// <summary>Bounded image-edit transport. Requests and images never appear in diagnostic events or errors.</summary>
public abstract class HttpImageEditClient(HttpClient http, Profile profile, string apiKey) : IImageEditClient
{
    protected Profile Profile { get; } = profile;
    protected string ApiKey { get; } = apiKey;
    public Action<LlmRequestMetadata>? ObserveRequest { get; set; }
    public const int MaximumImageBytes = 32 * 1024 * 1024;
    public const int MaximumResponseBytes = 64 * 1024 * 1024;
    public abstract HttpRequestMessage BuildRequest(ImageEditRequest request);
    protected abstract ImageEditResponse ParseResponse(JsonElement response);

    public async Task<ImageEditResponse> EditAsync(ImageEditRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Image.Length == 0 || request.Image.Length > MaximumImageBytes) throw new LlmException("AI 译图输入须为 1 字节到 32 MB 的图片。");
        if (request.MediaType is not ("image/png" or "image/jpeg" or "image/webp")) throw new LlmException("AI 译图仅支持 PNG、JPEG 或 WebP 图片。");
        if (string.IsNullOrWhiteSpace(Profile.Model)) throw new LlmException("请先填写图像编辑模型名称。");
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Profile.TimeoutSeconds)));
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                overall.Token.ThrowIfCancellationRequested();
                using var message = BuildRequest(request);
                var watch = Stopwatch.StartNew();
                HttpResponseMessage response;
                try { response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, overall.Token).ConfigureAwait(false); }
                catch (HttpRequestException) when (attempt < 2)
                {
                    Observe(message, null, watch.Elapsed.TotalMilliseconds, attempt);
                    await Task.Delay(TimeSpan.FromMilliseconds(attempt == 0 ? 500 : 1500), overall.Token).ConfigureAwait(false);
                    continue;
                }
                catch (HttpRequestException)
                {
                    Observe(message, null, watch.Elapsed.TotalMilliseconds, attempt);
                    throw new LlmException("AI 译图网络连接失败，请检查接口地址和代理。");
                }
                catch (OperationCanceledException)
                {
                    Observe(message, null, watch.Elapsed.TotalMilliseconds, attempt);
                    throw;
                }
                using (response)
                {
                    Observe(message, (int)response.StatusCode, watch.Elapsed.TotalMilliseconds, attempt);
                    bool retry = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                    if (retry && attempt < 2)
                    {
                        var delay = response.StatusCode == HttpStatusCode.TooManyRequests
                            ? response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date
                                ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(attempt == 0 ? 1 : 3))
                            : TimeSpan.FromMilliseconds(attempt == 0 ? 500 : 1500);
                        await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, overall.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode) throw Error((int)response.StatusCode);
                    byte[] body = await ReadBoundedAsync(response.Content, overall.Token).ConfigureAwait(false);
                    try
                    {
                        using var json = JsonDocument.Parse(body);
                        return ParseResponse(json.RootElement);
                    }
                    catch (JsonException) { throw new LlmException("AI 图像服务返回了无效的 JSON 数据。"); }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new LlmException("AI 译图请求超时，请增大模型超时时间后重试。"); }
        catch (IOException) { throw new LlmException("AI 图像服务响应中断，请重试。"); }
    }

    protected static ImageEditResponse DecodeImage(string? value, string? mediaType = null)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new LlmException("AI 服务未返回图片，请确认模型支持图像输出。");
        if (value.Length > (MaximumImageBytes + 2L) / 3 * 4) throw new LlmException("AI 服务返回的图片超过 32 MB 限制。");
        byte[] data;
        try { data = Convert.FromBase64String(value); }
        catch (FormatException) { throw new LlmException("AI 服务返回的图片编码无效。"); }
        if (data.Length == 0 || data.Length > MaximumImageBytes) throw new LlmException("AI 服务返回的图片为空或超出大小限制。");
        string actual = mediaType ?? (data.Length > 3 && data[0] == 0xff && data[1] == 0xd8 ? "image/jpeg" :
            data.Length > 12 && System.Text.Encoding.ASCII.GetString(data, 8, 4) == "WEBP" ? "image/webp" : "image/png");
        if (!actual.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) throw new LlmException("AI 服务返回了非图片内容。");
        return new(data, actual);
    }
    protected static Uri Endpoint(string baseUrl, string path)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new LlmException("图像接口地址须为不含凭证、查询参数或片段的 HTTP/HTTPS 地址。");
        return new(uri.AbsoluteUri.TrimEnd('/') + path);
    }
    private static LlmException Error(int status) => new(status switch
    {
        401 or 403 => "AI 图像接口的 API Key 无效或无权限，请检查配置。",
        404 => "AI 图像接口地址或模型名称错误。",
        429 => "AI 图像请求过于频繁，请稍后重试。",
        >= 500 => "AI 图像服务暂时不可用，请稍后重试。",
        _ => "AI 图像服务拒绝请求，请检查图片、模型能力和额外参数。"
    }, status);
    private void Observe(HttpRequestMessage request, int? status, double milliseconds, int attempt)
    {
        try { ObserveRequest?.Invoke(new("POST", request.RequestUri!.Host, status, milliseconds, attempt + 1)); }
        catch { }
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes) throw new LlmException("AI 图像响应超过 64 MB 限制。");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32768];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > MaximumResponseBytes) throw new LlmException("AI 图像响应超过 64 MB 限制。");
            output.Write(buffer, 0, read);
        }
    }
}
