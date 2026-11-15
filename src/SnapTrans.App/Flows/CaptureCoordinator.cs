using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Serilog;
using SkiaSharp;
using SnapTrans.App.Windows;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Settings;
using SnapTrans.Platform.Windows;

namespace SnapTrans.App.Flows;

public sealed class CaptureCoordinator : IDisposable
{
    private readonly IMonitorService _monitors;
    private readonly IScreenCapture _capture;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly List<OverlayWindow> _overlays = [];
    private IReadOnlyList<MonitorCapture>? _frozen;
    private nint _foreground;
    private Stopwatch? _watch;
    private CancellationTokenSource? _capturing;
    private int _generation;
    private (PixelRect Region, string Device, RequestedAction Action)? _last;
    public bool IsSelecting => _frozen is not null || _capturing is not null;
    public event EventHandler<CaptureSession>? Selected;
    public event EventHandler<string>? Failed;

    public CaptureCoordinator(IMonitorService monitors, IScreenCapture capture, AppSettings settings, Dispatcher dispatcher)
    {
        _monitors = monitors; _capture = capture; _settings = settings; _dispatcher = dispatcher;
        RebuildOverlays();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
    }
    private void DisplayChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        Cancel();
        try { RebuildOverlays(); }
        catch (Exception ex) { Failed?.Invoke(this, "显示器布局更新失败：" + ex.GetType().Name); }
    });
    private void RebuildOverlays()
    {
        foreach (var overlay in _overlays) overlay.Close();
        _overlays.Clear();
        foreach (var monitor in _monitors.GetMonitors())
        {
            var overlay = new OverlayWindow(monitor);
            overlay.SelectionCompleted += Complete;
            overlay.FirstFrameRendered += (_, _) => Log.Information("Overlay {Monitor} first frame drawn {ElapsedMs}ms", monitor.Index, _watch?.Elapsed.TotalMilliseconds);
            new WindowInteropHelper(overlay).EnsureHandle();
            _overlays.Add(overlay);
        }
    }
    public void Start(RequestedAction action) => BeginCapture(action, false);
    public void RepeatLast() => BeginCapture(_last?.Action ?? RequestedAction.Floating, true);
    private async void BeginCapture(RequestedAction action, bool repeat)
    {
        if (IsSelecting) return;
        if (repeat && _last is null) { Failed?.Invoke(this, "尚未选择过区域，请先框选一次。"); return; }
        int generation = ++_generation;
        using var captureCts = new CancellationTokenSource();
        _capturing = captureCts;
        try
        {
            _watch = Stopwatch.StartNew();
            _foreground = WindowPlacement.GetForegroundWindow();
            var monitors = _overlays.Select(o => o.Monitor).ToList();
            var snapshot = _settings.Capture.WindowSelection && !repeat ? new WindowDetectionService().Snapshot() : null;
            if (repeat && _last is { } last)
            {
                var monitor = monitors.FirstOrDefault(m => m.DeviceName == last.Device && m.Bounds.Contains(last.Region));
                if (monitor is null) throw new InvalidOperationException("显示器布局已改变，请重新框选区域。");
                monitors = [monitor];
            }
            // GDI freezes all screens synchronously; only black captures take the asynchronous WGC fallback.
            var captures = _capture is ScreenCaptureService service
                ? await service.CaptureAllAsync(monitors, _settings.Capture.UseGraphicsCaptureFallback, captureCts.Token)
                : _capture.CaptureAll(monitors);
            if (captureCts.IsCancellationRequested || generation != _generation)
            { foreach (var item in captures) item.Bitmap.Dispose(); return; }
            _frozen = captures;
            if (repeat && _last is { } previous)
            {
                var monitor = captures[0].Monitor;
                Complete(this, new SelectionResult(monitor.Index, new(previous.Region.X - monitor.Bounds.X, previous.Region.Y - monitor.Bounds.Y, previous.Region.Width, previous.Region.Height), action));
                return;
            }
            foreach (var frozen in _frozen)
                _overlays.Single(o => o.Monitor.Index == frozen.Monitor.Index)
                    .Prepare(frozen.Bitmap, _settings.General.ConfirmMode != "releaseToRun", action,
                        new SelectionAssistOptions(_settings.Capture.Magnifier, snapshot is null ? null : snapshot.Find));
            foreach (var overlay in _overlays) overlay.ShowCapture();
        }
        catch (OperationCanceledException) when (captureCts.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (generation != _generation) return;
            Cancel();
            Log.Warning("Capture failed {ErrorType}", e.GetType().Name);
            Failed?.Invoke(this, e is InvalidOperationException ? e.Message : "截图失败，请检查显示器是否仍已连接；受保护的视频和独占全屏内容可能无法捕获。");
        }
        finally { if (ReferenceEquals(_capturing, captureCts)) _capturing = null; }
    }
    private void Complete(object? sender, SelectionResult? result)
    {
        if (_frozen is null) return;
        foreach (var overlay in _overlays) overlay.HideCapture();
        WindowPlacement.RestoreForeground(_foreground);
        CaptureSession? session = null;
        try
        {
            if (result is null) return;
            var frozen = _frozen.Single(c => c.Monitor.Index == result.MonitorIndex);
            var region = result.RegionLocal.Intersect(new PixelRect(0, 0, frozen.Bitmap.Width, frozen.Bitmap.Height));
            if (region.Width < 8 || region.Height < 8) return;
            var image = new SKBitmap(region.Width, region.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(image))
                canvas.DrawBitmap(frozen.Bitmap, new SKRect(region.X, region.Y, region.Right, region.Bottom), new SKRect(0, 0, region.Width, region.Height));
            session = new CaptureSession
            {
                Monitor = frozen.Monitor,
                RegionVirtual = new PixelRect(frozen.Monitor.Bounds.X + region.X, frozen.Monitor.Bounds.Y + region.Y, region.Width, region.Height),
                Image = image, Action = result.Action
            };
            // A word lookup is a one-off: it must not replace the region that "repeat last" translates.
            if (result.Action != RequestedAction.Dictionary)
                _last = (session.RegionVirtual, frozen.Monitor.DeviceName, result.Action == RequestedAction.CopyText ? RequestedAction.Floating : result.Action);
        }
        finally { ReleaseFrozen(); }
        if (session is not null)
        {
            Log.Information("Capture {SessionId} {Width}x{Height} completed {ElapsedMs}ms", session.Id, session.Image.Width, session.Image.Height, _watch?.Elapsed.TotalMilliseconds);
            if (Selected is null) session.Dispose(); else Selected.Invoke(this, session);
        }
    }
    public void Cancel()
    {
        _generation++;
        _capturing?.Cancel();
        _capturing = null;
        bool selecting = _frozen is not null;
        foreach (var overlay in _overlays) overlay.HideCapture();
        ReleaseFrozen();
        if (selecting && _foreground != 0) WindowPlacement.RestoreForeground(_foreground);
    }
    private void ReleaseFrozen()
    {
        if (_frozen is not null) foreach (var frozen in _frozen) frozen.Bitmap.Dispose();
        _frozen = null;
    }
    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        Cancel();
        foreach (var overlay in _overlays) overlay.Close();
        _overlays.Clear();
    }
}
