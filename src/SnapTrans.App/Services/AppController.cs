using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using SnapTrans.App.Flows;
using SnapTrans.App.Tray;
using SnapTrans.App.Windows;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Hotkeys;
using SnapTrans.Core.Layout;
using SnapTrans.Core.Ocr;
using SnapTrans.Core.Settings;
using SnapTrans.Core.Translation;
using SnapTrans.Platform.Windows;
using SnapTrans.Core.Caching;
using SnapTrans.Core.Data;

namespace SnapTrans.App.Services;

public sealed class AppController : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly IHotkeyService _hotkeys;
    private readonly IOcrEngine _ocr;
    private readonly IClipboardService _clipboard;
    private readonly ISecretProtector _secrets;
    private readonly IMonitorService _monitors;
    private readonly AutoStartService _autoStart;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _saveTimer;
    private readonly CaptureCoordinator _capture;
    private readonly FloatingTranslateFlow _floating;
    private readonly ImageTranslateFlow _image;
    private readonly DictionaryFlow _dictionary;
    private readonly TrayIcon _tray;
    private readonly MemoryTranslationCache _cache;
    private readonly HistoryStore _history;
    private readonly OcrServices _ocrServices;
    private readonly AppUpdateService _updates;
    private readonly PaddleSetupService _paddle;
    private SettingsWindow? _settingsWindow;
    private string[] _oldHotkeys;
    private bool _paused;
    private bool _registered;
    private bool _recordingHotkey;
    private bool _hotkeyWarned;
    private bool _disposed;

    public AppController(AppSettings settings, SettingsStore store, IHotkeyService hotkeys, IOcrEngine ocr,
        IClipboardService clipboard, ISecretProtector secrets, IMonitorService monitors, IScreenCapture capture, AutoStartService autoStart,
        MemoryTranslationCache cache, HistoryStore history, OcrServices ocrServices, AppUpdateService updates)
    {
        _settings = settings; _store = store; _hotkeys = hotkeys; _ocr = ocr; _clipboard = clipboard;
        _secrets = secrets; _monitors = monitors; _autoStart = autoStart; _dispatcher = Application.Current.Dispatcher;
        _oldHotkeys = HotkeyValues(); _cache = cache; _history = history; _ocrServices = ocrServices; _updates = updates;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SaveNowAsync(); };
        _capture = new CaptureCoordinator(monitors, capture, settings, _dispatcher);
        _floating = new FloatingTranslateFlow(settings, ocr, clipboard, secrets, SaveSoon, Consent, cache, history);
        _image = new ImageTranslateFlow(settings, ocr, clipboard, secrets, Consent, cache, history);
        _dictionary = new DictionaryFlow(settings, ocr, clipboard, monitors, capture, message => _tray!.Info(message));
        _paddle = new PaddleSetupService(settings, ocrServices, () => _ocr as OcrEngineRegistry);
        _tray = new TrayIcon(new TrayCommands
        {
            CaptureText = () => Capture(RequestedAction.Floating),
            CaptureImage = () => Capture(RequestedAction.Image),
            RepeatLast = _capture.RepeatLast,
            CaptureWord = () => Capture(RequestedAction.Dictionary),
            Settings = OpenSettings,
            OpenPictures = () => OpenFolder(_settings.ImageTranslate.SaveFolder),
            Pause = paused =>
            {
                _paused = paused;
                if (paused) { _hotkeys.UnregisterAll(); _registered = false; } else TryApplySettings();
            },
            PixelTest = ShowPixelTest,
            About = ShowAbout,
            Exit = async () =>
            {
                _saveTimer.Stop();
                _settingsWindow?.Close();
                await SaveNowAsync();
                Application.Current.Shutdown();
            },
            Hotkeys = () => (_settings.Hotkeys.TranslateFloating, _settings.Hotkeys.TranslateImage, _settings.Hotkeys.RepeatLastRegion,
                _settings.Dictionary.Mode == "capture" ? _settings.Hotkeys.Dictionary : "")
        });
        _capture.Selected += async (_, session) =>
        {
            try
            {
                if (session.Action == RequestedAction.Image) await _image.ShowAsync(session, true);
                else if (session.Action == RequestedAction.CopyText) await CopyTextAsync(session);
                else if (session.Action == RequestedAction.Dictionary) await _dictionary.ShowAsync(session);
                else await _floating.ShowAsync(session);
            }
            catch (Exception e) { session.Dispose(); Report("无法打开结果窗口", e); }
        };
        _capture.Failed += (_, error) => _tray.Error(error);
        _floating.ImageRequested += async (_, request) =>
        {
            try { await _image.ShowAsync(request.Session, false, request.Ai ? true : null, request.TargetLanguage); }
            catch (Exception e) { Report("无法生成译图", e); }
        };
        _hotkeys.Pressed += HotkeyPressed;
    }
    public void Start()
    {
        TryApplySettings();
        if (_settings.Updates.AutoCheck) _ = CheckUpdatesAtStartupAsync();
        if (_store.WasRecovered) _tray.Error("配置文件已损坏，已备份并恢复默认设置。");
        if (!_settings.General.OnboardingCompleted)
        {
            var welcome = new WelcomeWindow(OpenSettings, () => Capture(RequestedAction.Floating));
            welcome.Closed += (_, _) => { _settings.General.OnboardingCompleted = true; SaveSoon(); };
            welcome.Show();
        }
    }
    private void HotkeyPressed(object? sender, HotkeyId id)
    {
        if (id == HotkeyId.RepeatLastRegion) _capture.RepeatLast();
        else if (id == HotkeyId.Dictionary) LookupWord();
        else Capture(id == HotkeyId.TranslateImage ? RequestedAction.Image : RequestedAction.Floating);
    }
    public void Capture(RequestedAction action) => _capture.Start(action);
    public void ShowTrayMenu() => _tray.ShowMenu();
    /// <summary>The dictionary hotkey: the word at the pointer, or a region to draw, as chosen in settings.</summary>
    public async void LookupWord()
    {
        if (_settings.Dictionary.Mode == "capture") { Capture(RequestedAction.Dictionary); return; }
        if (_capture.IsSelecting) return;
        try { await _dictionary.LookupAtCursorAsync(); }
        catch (Exception e) { Report("查词失败", e); }
    }
    /// <summary>Opens the dictionary card for a given word (command line).</summary>
    public void LookupWord(string word)
    {
        try { _dictionary.Lookup(word); }
        catch (Exception e) { Report("查词失败", e); }
    }
    private string[] HotkeyValues() => [_settings.Hotkeys.TranslateFloating, _settings.Hotkeys.TranslateImage, _settings.Hotkeys.RepeatLastRegion, _settings.Hotkeys.Dictionary];
    private async Task CheckUpdatesAtStartupAsync()
    {
        try { await _updates.CheckAsync(CancellationToken.None); if (_updates.HasUpdate) _tray.Info("有新版本可用，可在设置 → 高级 → 更新中安装。"); }
        catch (Exception e) { Log.Warning("Update check failed {ErrorType}", e.GetType().Name); }
    }
    private void CopySettingsWithoutSanitizing(AppSettings snapshot)
    {
        foreach (var property in typeof(AppSettings).GetProperties().Where(p => p.CanRead && p.CanWrite))
            property.SetValue(_settings, property.GetValue(snapshot));
    }
    private async Task<string> InstallPaddleFromFileAsync(CancellationToken ct)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择 PaddleOCR 组件 ZIP", Filter = "PaddleOCR 组件 ZIP|*.zip", CheckFileExists = true };
        if (picker.ShowDialog(_settingsWindow) != true) return "已取消。";
        if (MessageBox.Show(_settingsWindow, "组件包含本机代码，请只安装从官方发布页下载的 ZIP。是否安装？", "安装 PaddleOCR 组件", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return "已取消。";
        return await _paddle.InstallArchiveAsync(picker.FileName, ct);
    }
    public void OpenSettings()
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(OpenSettings); return; }
        if (_settingsWindow is not null) { _settingsWindow.Show(); _settingsWindow.Activate(); return; }
        var features = new SettingsFeatureServices
        {
            Cache = _cache, CacheStatistics = _cache.GetStatistics, History = _history,
            StoragePaths = AppPaths.Paths, GetOcrModels = _ocrServices.GetModelManager,
            ApplyImportedSettingsAsync = async (imported, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var previous = SettingsSnapshot.Clone(_settings);
                try { ConfigurationTransfer.CopyInto(_settings, imported); ApplySettings(); await _store.SaveAsync(SettingsSnapshot.Clone(_settings), ct); }
                catch { CopySettingsWithoutSanitizing(previous); TryApplySettings(); throw; }
                await _cache.ClearAsync(ct);
            },
            CheckForUpdatesAsync = _updates.CheckAsync,
            HasAvailableUpdate = () => _updates.HasUpdate,
            InstallUpdateAsync = (progress, ct) => _updates.InstallAsync(progress, () => _store.SaveAsync(SettingsSnapshot.Clone(_settings), ct), ct),
            IsPaddlePluginAvailable = () => (_ocr as OcrEngineRegistry)?.IsPluginAvailable ?? false,
            SetupPaddleAsync = _paddle.InstallAsync,
            InstallPaddleFromFileAsync = InstallPaddleFromFileAsync,
            HotkeyRecordingChanged = recording =>
            {
                if (_disposed) return;
                _recordingHotkey = recording;
                if (recording) { _hotkeys.UnregisterAll(); _registered = false; } else ApplySettings();
            }
        };
        _settingsWindow = new SettingsWindow(_settings, () => { _ = SaveNowAsync(); }, _secrets, _ocr, TestConnectionAsync, ApplySettings, features);
        _settingsWindow.Closed += async (_, _) => { _settingsWindow = null; await SaveNowAsync(); };
        _settingsWindow.Show(); _settingsWindow.Activate();
    }
    private async Task<string> TestConnectionAsync(Profile profile, bool vision, CancellationToken ct)
    {
        try
        {
            profile = SettingsSnapshot.Clone(profile);
            if (!Consent(profile)) return "已取消连接测试。";
            using var connection = new ModelConnection(profile, _settings.Network, _secrets);
            var result = await ConnectionTester.TestAsync(connection.Client, profile, vision, ct);
            return vision ? $"文字：{result.TextMessage}\n图像：{result.VisionMessage}" : $"文字：{result.TextMessage}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return "连接测试已取消。"; }
        catch (Exception e) { return FloatingTranslateFlow.UserError(e); }
    }
    private bool Consent(Profile profile)
    {
        if (Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback) return true;
        if (_settings.General.CloudPrivacyNoticeAccepted) return true;
        if (!_dispatcher.CheckAccess()) return _dispatcher.Invoke(() => Consent(profile));
        bool accepted = MessageBox.Show("截图或识别出的文字将发送给你配置的模型服务，可能产生费用。\n\n请确认服务可信，并避免框选敏感内容。是否继续？",
            "屏译 · 发送到云端", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (accepted) { _settings.General.CloudPrivacyNoticeAccepted = true; SaveSoon(); }
        return accepted;
    }
    private async Task CopyTextAsync(CaptureSession session)
    {
        using var lease = session.Acquire();
        try
        {
            var settings = SettingsSnapshot.Clone(_settings);
            var request = new OcrRequest(settings.Ocr.Language == "auto" ? null : settings.Ocr.Language,
                double.TryParse(settings.Ocr.Scale, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale) ? scale : 0);
            var engine = _ocr is OcrEngineRegistry registry ? registry.Snapshot() : _ocr;
            var availability = engine.CheckAvailability(request.LanguageTag);
            if (!availability.IsAvailable) throw new InvalidOperationException(availability.Reason + "\n" + availability.RepairInstructions);
            var page = await session.GetOcrAsync(engine, request);
            var text = string.Join(Environment.NewLine, new BlockBuilder().Build(page).Select(b => b.SourceText));
            if (string.IsNullOrWhiteSpace(text)) _tray.Info("未识别到文字。");
            else { await _clipboard.SetTextAsync(text); _tray.Info("识别文字已复制。"); }
        }
        catch (Exception e) { Report("识别失败", e); }
        finally { session.Dispose(); }
    }
    private void TryApplySettings()
    {
        try { ApplySettings(); }
        catch (Exception e) { _tray.Error(e.Message); }
    }
    private void ApplySettings()
    {
        if (_disposed) return;
        AppLog.Level.MinimumLevel = _settings.General.DebugLogging ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information;
        Controls.SystemTheme.SetMode(_settings.General.Theme);
        var hotkeys = HotkeyValues();
        if (!_paused && !_recordingHotkey && (!_registered || !hotkeys.SequenceEqual(_oldHotkeys)))
        {
            if (TryRegisterHotkeys(hotkeys, out string? error))
            {
                _oldHotkeys = hotkeys;
                _registered = true;
                Log.Information("Hotkeys registered");
            }
            else if (hotkeys.SequenceEqual(_oldHotkeys))
            {
                // Nothing was edited (startup or resume): keep the gestures that did register and say so once.
                _registered = true;
                Log.Warning("Hotkey registration incomplete");
                if (!_hotkeyWarned) { _hotkeyWarned = true; _tray.Error(error!); }
            }
            else
            {
                // An edit could not be registered: restore the previous gestures and settings.
                TryRegisterHotkeys(_oldHotkeys, out _);
                _settings.Hotkeys.TranslateFloating = _oldHotkeys[0]; _settings.Hotkeys.TranslateImage = _oldHotkeys[1];
                _settings.Hotkeys.RepeatLastRegion = _oldHotkeys[2]; _settings.Hotkeys.Dictionary = _oldHotkeys[3];
                Log.Warning("Hotkey registration failed");
                throw new InvalidOperationException(error ?? "快捷键已被其他程序占用。");
            }
        }
        if (_autoStart.IsEnabled() != _settings.General.LaunchAtStartup) _autoStart.SetEnabled(_settings.General.LaunchAtStartup);
        // Refresh display affinity immediately for already visible result windows.
        foreach (Window window in Application.Current.Windows)
            if (window is FloatingResultWindow or ImageResultWindow)
                WindowPlacement.SetCaptureExcluded(window, _settings.General.ExcludeOwnWindowsFromCapture);
            else if (window is DictionaryWindow)
            {
                // A layered window may refuse the capture-exclusion flag; that must not fail saving settings.
                try { WindowPlacement.SetCaptureExcluded(window, _settings.General.ExcludeOwnWindowsFromCapture); }
                catch (System.ComponentModel.Win32Exception) { }
            }
    }
    /// <summary>Registers every bound gesture; the first two are required, the others may be empty.</summary>
    private bool TryRegisterHotkeys(string[] values, out string? error)
    {
        HotkeyId[] ids = [HotkeyId.TranslateFloating, HotkeyId.TranslateImage, HotkeyId.RepeatLastRegion, HotkeyId.Dictionary];
        string[] names = ["截图翻译", "截图译图", "重复上次区域", "查词"];
        _hotkeys.UnregisterAll();
        error = null;
        for (int index = 0; index < ids.Length; index++)
        {
            if (index >= 2 && string.IsNullOrWhiteSpace(values[index])) continue;
            if (!_hotkeys.TryRegister(ids[index], HotkeyGesture.Parse(values[index]), out string? reason))
                error ??= $"{names[index]}（{values[index]}）：{reason ?? "快捷键已被其他程序占用。"}";
        }
        return error is null;
    }
    public void SaveSoon() { if (!_disposed) { _saveTimer.Stop(); _saveTimer.Start(); } }
    public async Task SaveNowAsync()
    {
        try { await _store.SaveAsync(SettingsSnapshot.Clone(_settings)); }
        catch (Exception e) { if (!_disposed) Report("配置保存失败", e); }
    }
    private void ShowPixelTest() { foreach (var monitor in _monitors.GetMonitors()) new PixelTestWindow(monitor).Show(); }
    private static void ShowAbout() => MessageBox.Show($"屏译 SnapTrans {PaddleSetupService.AppVersion}", "关于", MessageBoxButton.OK, MessageBoxImage.Information);
    public static void OpenFolder(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path); Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    public void Report(string message, Exception e)
    {
        Log.Warning("{Operation} failed {ErrorType} {Stack}", message, e.GetType().Name, e.StackTrace);
        _tray.Error(message + "：" + FloatingTranslateFlow.UserError(e));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _saveTimer.Stop(); _hotkeys.Pressed -= HotkeyPressed; _hotkeys.UnregisterAll();
        _capture.Dispose(); _floating.Dispose(); _image.Dispose(); _dictionary.Dispose();
        _settingsWindow?.Close(); _tray.Dispose();
    }
}
