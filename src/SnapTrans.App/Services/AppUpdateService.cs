using SnapTrans.Core.Settings;
using Velopack;
using Velopack.Sources;

namespace SnapTrans.App.Services;

/// <summary>Explicit, user-controlled Velopack updates. Never installs code during a check.</summary>
public sealed class AppUpdateService
{
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateManager? _manager;
    private UpdateInfo? _available;
    private UpdateSettings? _checkedSettings;
    public bool HasUpdate => _available is not null;
    public AppUpdateService(AppSettings settings) => _settings = settings;

    public async Task<string> CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (AppPaths.IsPortable)
            {
                _available = null;
                return "便携模式需要手动更新：备份 portable.txt、settings.json 和 data 后，用新版 ZIP 覆盖程序文件。";
            }
            var settings = SettingsSnapshot.Clone(_settings.Updates);
            if (!Uri.TryCreate(settings.FeedUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("更新源须为不含凭证、查询参数和片段的 HTTPS 地址。");
            _available = null;
            _checkedSettings = settings;
            var options = new UpdateOptions { ExplicitChannel = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64" };
            _manager = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? new UpdateManager(new GithubSource(uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), null, settings.IncludePrerelease), options)
                : new UpdateManager(uri.AbsoluteUri, options);
            if (!_manager.IsInstalled)
                return "当前安装方式不支持应用内更新，请到发布页下载新版。";
            _available = await _manager.CheckForUpdatesAsync().WaitAsync(ct);
            if (!MatchesCurrentSettings()) { _available = null; return "更新源或预发布设置已改变，请重新检查更新。"; }
            return _available is null ? "当前已是最新版本。" : $"发现新版本 {_available.TargetFullRelease.Version}，点击“安装”开始下载。";
        }
        finally { _gate.Release(); }
    }
    public async Task<string> InstallAsync(IProgress<int>? progress, Func<Task> beforeRestart, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (AppPaths.IsPortable) { _available = null; return "便携模式需要手动更新。"; }
            if (_manager is null || _available is null) return "请先检查更新。";
            if (!MatchesCurrentSettings()) { _available = null; return "更新源或预发布设置已改变，请重新检查更新。"; }
            await _manager.DownloadUpdatesAsync(_available, percent => progress?.Report(percent), ct);
            ct.ThrowIfCancellationRequested();
            if (!MatchesCurrentSettings()) { _available = null; return "更新设置已改变，已取消安装；请重新检查。"; }
            await beforeRestart();
            ct.ThrowIfCancellationRequested();
            if (!MatchesCurrentSettings()) { _available = null; return "更新设置已改变，已取消安装；请重新检查。"; }
            _manager.ApplyUpdatesAndRestart(_available.TargetFullRelease);
            return "更新已准备，应用即将重启。";
        }
        finally { _gate.Release(); }
    }
    private bool MatchesCurrentSettings() => _checkedSettings is not null
        && _checkedSettings.FeedUrl == _settings.Updates.FeedUrl
        && _checkedSettings.IncludePrerelease == _settings.Updates.IncludePrerelease;
}
