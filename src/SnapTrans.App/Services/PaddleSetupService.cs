using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Serilog;
using SnapTrans.Core.Ocr;
using SnapTrans.Core.Settings;
using SnapTrans.Platform.Windows;

namespace SnapTrans.App.Services;

/// <summary>What the one-click setup is doing; <see cref="Percent"/> is null while a step has no measurable progress.</summary>
public sealed record PaddleSetupProgress(string Message, double? Percent);

/// <summary>
/// One-click PaddleOCR setup: downloads the component that matches this build from the release feed,
/// installs it through the validating archive installer, downloads and verifies the model, then enables the engine.
/// </summary>
public sealed class PaddleSetupService(AppSettings settings, OcrServices ocr, Func<OcrEngineRegistry?> registry)
{
    private const long MaxComponentBytes = 512L * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string Runtime => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    public static string AppVersion => typeof(PaddleSetupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(PaddleSetupService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>GitHub feeds serve release assets under a version tag; any other HTTPS feed is a flat folder.</summary>
    public static Uri ComponentUri(string feedUrl, string version, string runtime)
    {
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var feed) || feed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(feed.UserInfo))
            throw new InvalidOperationException("更新源必须是 HTTPS 地址。");
        string file = $"SnapTrans-{version}-{runtime}-paddle.zip";
        string root = feed.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri(feed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? $"{root}/releases/download/v{version}/{file}"
            : $"{root}/{file}");
    }

    /// <summary>Runs every missing step. Returns a short status for the settings page.</summary>
    public async Task<string> InstallAsync(string presetId, IProgress<PaddleSetupProgress>? progress, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return "安装正在进行。";
        try
        {
            var engines = registry() ?? throw new InvalidOperationException("当前启动方式不支持 PaddleOCR。");
            if (!engines.IsPluginAvailable)
            {
                string archive = Path.Combine(Path.GetTempPath(), $"SnapTrans-paddle-{Guid.NewGuid():N}.zip");
                try
                {
                    var uri = ComponentUri(settings.Updates.FeedUrl, AppVersion, Runtime);
                    progress?.Report(new("正在下载组件…", 0));
                    await DownloadAsync(uri, archive, (received, total) =>
                        progress?.Report(new($"正在下载组件 {received / 1048576d:F1}{(total is > 0 ? $" / {total.Value / 1048576d:F1}" : "")} MB", total is > 0 ? received * 100d / total.Value : null)), ct);
                    progress?.Report(new("正在安装组件…", null));
                    await InstallArchiveAsync(archive, ct);
                }
                finally { TryDelete(archive); }
                if (!engines.IsPluginAvailable) throw new InvalidOperationException("组件已安装但无法加载，请重启屏译后重试。");
            }
            var models = ocr.GetModelManager();
            if (!(await models.GetStatusAsync(presetId, ct)).IsReady)
            {
                progress?.Report(new("正在下载模型…", 0));
                await models.DownloadAsync(presetId, new Progress<OcrModelDownloadProgress>(value =>
                    progress?.Report(new($"正在下载模型 {value.DownloadedBytes / 1048576d:F1} / {value.TotalBytes / 1048576d:F1} MB", value.Percentage))), ct);
                var status = await models.GetStatusAsync(presetId, ct);
                if (!status.IsReady) throw new InvalidOperationException(status.Message);
            }
            settings.Ocr.ModelPreset = presetId;
            settings.Ocr.Engine = "paddle";
            Log.Information("PaddleOCR one-click setup completed {Preset}", presetId);
            return "PaddleOCR 已安装并启用。";
        }
        finally { _gate.Release(); }
    }

    /// <summary>Installs a component ZIP the user picked from disk.</summary>
    public async Task<string> InstallArchiveAsync(string zipPath, CancellationToken ct)
    {
        try
        {
            var result = await OcrPluginArchive.InstallAsync(zipPath, AppPaths.PluginFolder, Runtime, ct);
            return $"组件 {result.Version} 已安装。";
        }
        catch (OcrPluginInstallException e) when (e.RequiresRestart)
        { throw new InvalidOperationException("组件正在使用。请切换到 Windows OCR 并重启屏译后再安装。", e); }
        catch (OcrPluginInstallException e) { throw new InvalidOperationException(e.Message, e); }
    }

    /// <summary>
    /// Streams the component to disk. Redirects are followed by hand so that every hop stays on HTTPS
    /// and inside the release host family; the archive installer then verifies each file's SHA-256.
    /// </summary>
    private async Task DownloadAsync(Uri uri, string destination, Action<long, long?> report, CancellationToken ct)
    {
        using var http = HttpTransport.Create(settings.Network);
        http.Timeout = TimeSpan.FromMinutes(20);
        string origin = uri.Host;
        for (int hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("SnapTrans");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                if (hop >= 5) throw new InvalidOperationException("组件下载重定向次数过多。");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (uri.Scheme != Uri.UriSchemeHttps || !IsTrustedHost(uri.Host, origin)) throw new InvalidOperationException("组件下载被重定向到了不受信任的地址。");
                continue;
            }
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new InvalidOperationException($"发布页没有可下载的 {AppVersion} 组件（HTTP {(int)response.StatusCode}）。可改用“从文件安装”。");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"组件下载失败（HTTP {(int)response.StatusCode}），请检查网络或代理。");
            long? total = response.Content.Headers.ContentLength;
            if (total > MaxComponentBytes) throw new InvalidOperationException("组件文件超出大小限制。");
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long received = 0, reported = 0;
            while (true)
            {
                int count = await source.ReadAsync(buffer, ct);
                if (count == 0) break;
                received += count;
                if (received > MaxComponentBytes) throw new InvalidOperationException("组件文件超出大小限制。");
                await target.WriteAsync(buffer.AsMemory(0, count), ct);
                if (received - reported >= 262144) { reported = received; report(received, total); }
            }
            if (total is { } expected && received != expected) throw new InvalidOperationException("组件下载不完整，请重试。");
            report(received, total);
            return;
        }
    }

    private static bool IsTrustedHost(string host, string origin) =>
        host.Equals(origin, StringComparison.OrdinalIgnoreCase)
        || (origin.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && (host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
