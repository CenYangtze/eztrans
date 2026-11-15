using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SnapTrans.App.Services;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Ocr;
using SnapTrans.Core.Settings;
using SnapTrans.Platform.Windows;
using SnapTrans.Core.Caching;
using SnapTrans.Core.Data;

namespace SnapTrans.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private IHost? _host;
    private AppController? _controller;
    private Task? _listener;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _instance = new SingleInstance();
            if (!_instance.IsFirst)
            {
                // "--menu" opens the running instance's tray menu, "--lookup [word]" its dictionary; any other launch opens settings.
                int lookup = Array.FindIndex(e.Args, a => a.Equals("--lookup", StringComparison.OrdinalIgnoreCase));
                await _instance.NotifyAsync(lookup >= 0 ? SingleInstance.LookupCommand(lookup + 1 < e.Args.Length ? e.Args[lookup + 1] : null)
                    : e.Args.Contains("--menu", StringComparer.OrdinalIgnoreCase) ? SingleInstance.Menu : SingleInstance.Settings);
                Shutdown(); return;
            }
            SnapTrans.App.Controls.SystemTheme.Initialize();
            Directory.CreateDirectory(AppPaths.LogFolder);
            Log.Logger = new LoggerConfiguration().MinimumLevel.ControlledBy(AppLog.Level)
                .WriteTo.File(Path.Combine(AppPaths.LogFolder, "snaptrans-.log"), rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7, fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true)
                .CreateLogger();
            Log.Information("SnapTrans starting {Version}", typeof(App).Assembly.GetName().Version);
            RegisterExceptions();
            var store = new SettingsStore(Path.Combine(AppPaths.SettingsFolder, "settings.json"));
            var settings = await store.LoadAsync();
            SnapTrans.App.Controls.SystemTheme.SetMode(settings.General.Theme);
            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(settings); services.AddSingleton(store);
                    services.AddSingleton<IMonitorService, MonitorService>();
                    services.AddSingleton<IScreenCapture>(_ => new ScreenCaptureService());
                    services.AddSingleton<IHotkeyService, HotkeyService>();
                    services.AddSingleton<WindowsOcrEngine>();
                    services.AddSingleton<OcrServices>();
                    services.AddSingleton<IOcrEngine>(sp => new OcrEngineRegistry(sp.GetRequiredService<WindowsOcrEngine>(),
                        () => settings.Ocr.Engine, sp.GetRequiredService<OcrServices>().GetModelManager, () => settings.Ocr.ModelPreset, AppPaths.PluginFolder));
                    services.AddSingleton<MemoryTranslationCache>(_ => new MemoryTranslationCache(() => settings.Cache));
                    services.AddSingleton<HistoryStore>(_ => new HistoryStore(AppPaths.HistoryFile, () => settings.History));
                    services.AddSingleton<AppUpdateService>();
                    services.AddSingleton<IClipboardService, ClipboardService>();
                    services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
                    services.AddSingleton<AutoStartService>(); services.AddSingleton<AppController>();
                }).Build();
            await _host.StartAsync();
            _controller = _host.Services.GetRequiredService<AppController>();
            _controller.Start();
            _listener = _instance.ListenAsync(command => Dispatcher.BeginInvoke(() =>
            {
                if (command == SingleInstance.Menu) _controller.ShowTrayMenu();
                else if (command == SingleInstance.Lookup) _controller.LookupWord();
                else if (SingleInstance.LookupWord(command) is { } word) _controller.LookupWord(word);
                else _controller.OpenSettings();
            }));
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed {ErrorType} {Stack}", ex.GetType().Name, ex.StackTrace);
            MessageBox.Show("屏译启动失败（" + ex.GetType().Name + "）。\n请确认已安装 .NET 10 Desktop Runtime，并查看日志：\n" + AppPaths.LogFolder,
                "屏译", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void RegisterExceptions()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            if (_controller is not null) _controller.Report("界面操作失败", e.Exception);
            else MessageBox.Show("屏译遇到错误，请查看日志后重试。", "屏译");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task error {ErrorType}", e.Exception.GetType().Name); e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Fatal("Fatal error {ErrorType} {Stack}", ex.GetType().Name, ex.StackTrace);
            Log.CloseAndFlush();
        };
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose(); _host?.Dispose(); _instance?.Dispose();
        Log.Information("SnapTrans exited"); Log.CloseAndFlush();
        base.OnExit(e);
    }
}
