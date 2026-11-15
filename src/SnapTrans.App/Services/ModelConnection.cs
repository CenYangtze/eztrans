using System.Net;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Llm;
using SnapTrans.Core.Settings;
using SnapTrans.Core.Caching;

namespace SnapTrans.App.Services;

/// <summary>Each operation snapshots proxy and profile settings; no stale pooled credentials after edits.</summary>
public sealed class ModelConnection : IDisposable
{
    public HttpClient Http { get; }
    public ILlmClient Client { get; }
    public ModelConnection(Profile profile, NetworkSettings network, ISecretProtector secrets, ITranslationCache? cache = null)
    {
        profile = SettingsSnapshot.Clone(profile);
        network = SettingsSnapshot.Clone(network);
        Http = HttpTransport.Create(network);
        try
        {
            string key = string.IsNullOrEmpty(profile.ApiKeyProtected) ? "" : secrets.Unprotect(profile.ApiKeyProtected);
            var client = LlmClientFactory.Create(Http, profile, key, m =>
                Serilog.Log.Information("HTTP {Method} {Host} {StatusCode} {ElapsedMs}ms attempt={Attempt}",
                    m.Method, m.Host, m.StatusCode, m.ElapsedMilliseconds, m.Attempt));
            Client = cache is null ? client : new CachedLlmClient(client, profile, cache);
        }
        catch { Http.Dispose(); throw; }
    }
    public void Dispose() => Http.Dispose();
}

internal static class HttpTransport
{
    public static HttpClient Create(NetworkSettings network)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10), AllowAutoRedirect = false };
        if (network.Proxy == "none") handler.UseProxy = false;
        else if (network.Proxy == "custom")
        {
            if (!Uri.TryCreate(network.CustomProxyUrl, UriKind.Absolute, out var proxy) || proxy.Scheme is not ("http" or "https" or "socks5"))
                throw new InvalidOperationException("自定义代理地址无效，请使用 http:// 或 socks5:// 地址。");
            handler.Proxy = new WebProxy(proxy);
        }
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
