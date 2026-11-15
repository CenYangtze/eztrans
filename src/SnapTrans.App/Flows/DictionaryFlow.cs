using System.Diagnostics;
using System.Globalization;
using Serilog;
using SnapTrans.App.Services;
using SnapTrans.App.Windows;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Dictionaries;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Ocr;
using SnapTrans.Core.Settings;
using SnapTrans.Platform.Windows;
using Forms = System.Windows.Forms;

namespace SnapTrans.App.Flows;

/// <summary>
/// Word lookup: finds the English word next to the pointer (or in a drawn region) with OCR,
/// asks the dictionary service and shows the answer in a <see cref="DictionaryWindow"/>.
/// </summary>
public sealed class DictionaryFlow : IDisposable
{
    // Half of the area read around the pointer, in device-independent pixels: a few words either side, three lines.
    private const double HalfWidthDip = 280, HalfHeightDip = 70;

    private sealed class State(DictionaryWindow window)
    {
        public DictionaryWindow Window { get; } = window;
        public CancellationTokenSource? Run { get; set; }
    }

    private readonly AppSettings _settings;
    private readonly IOcrEngine _ocr;
    private readonly IClipboardService _clipboard;
    private readonly IMonitorService _monitors;
    private readonly IScreenCapture _capture;
    private readonly Action<string> _notify;
    private readonly DictionaryService _service;
    private readonly List<State> _windows = [];
    private bool _locating;

    public DictionaryFlow(AppSettings settings, IOcrEngine ocr, IClipboardService clipboard, IMonitorService monitors, IScreenCapture capture, Action<string> notify)
    {
        _settings = settings; _ocr = ocr; _clipboard = clipboard; _monitors = monitors; _capture = capture; _notify = notify;
        Func<HttpClient> client = () => HttpTransport.Create(settings.Network);
        _service = new DictionaryService([new YoudaoDictionary(client), new GoogleDictionary(client)]);
    }

    /// <summary>Looks up the word closest to the mouse pointer.</summary>
    public async Task LookupAtCursorAsync()
    {
        if (_locating) return;
        _locating = true;
        try
        {
            var cursor = Forms.Cursor.Position;
            var point = new PixelPoint(cursor.X, cursor.Y);
            var monitor = _monitors.GetMonitors().FirstOrDefault(m => m.Bounds.Contains(point));
            if (monitor is null) return;
            int halfWidth = (int)Math.Round(HalfWidthDip * monitor.Scale), halfHeight = (int)Math.Round(HalfHeightDip * monitor.Scale);
            var region = new PixelRect(point.X - halfWidth, point.Y - halfHeight, halfWidth * 2, halfHeight * 2).Intersect(monitor.Bounds);
            if (region.Width < 16 || region.Height < 16) return;
            var watch = Stopwatch.StartNew();
            WordPicker.Hit? hit;
            using (var image = _capture.CaptureRegion(region))
            {
                var page = await RecognizeAsync(image, CancellationToken.None);
                hit = WordPicker.Nearest(page, point.X - region.X, point.Y - region.Y);
            }
            Log.Information("Dictionary word located {Found} {ElapsedMs}ms", hit is not null, watch.Elapsed.TotalMilliseconds);
            if (hit is null) { _notify("光标附近没有识别到英文单词。"); return; }
            var anchor = new PixelRect(region.X + (int)hit.Bounds.Left, region.Y + (int)hit.Bounds.Top,
                Math.Max(1, (int)Math.Ceiling(hit.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(hit.Bounds.Height)));
            Show(hit.Word, anchor, monitor);
        }
        finally { _locating = false; }
    }

    /// <summary>Looks up the text of a region the user drew; the session is disposed here.</summary>
    public async Task ShowAsync(CaptureSession session)
    {
        try
        {
            using var lease = session.Acquire();
            var page = await RecognizeAsync(session.Image, session.Cts.Token);
            string? query = WordPicker.Phrase(page);
            if (query is null) { _notify("所选区域没有识别到可查询的文字。"); return; }
            Show(query, session.RegionVirtual, session.Monitor);
        }
        finally { session.Dispose(); }
    }

    /// <summary>Looks up a word that did not come from the screen (command line); the card opens at the pointer.</summary>
    public void Lookup(string word)
    {
        if (WordPicker.Normalize(word) is not { } query) return;
        var cursor = Forms.Cursor.Position;
        var point = new PixelPoint(cursor.X, cursor.Y);
        var monitors = _monitors.GetMonitors();
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(point)) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (monitor is null) return;
        Show(query, new PixelRect(point.X, point.Y, 1, 1), monitor);
    }

    private async Task<OcrPage> RecognizeAsync(SkiaSharp.SKBitmap image, CancellationToken ct)
    {
        var engine = _ocr is OcrEngineRegistry registry ? registry.Snapshot() : _ocr;
        string? language = _settings.Ocr.Language == "auto" ? null : _settings.Ocr.Language;
        // Only English words are wanted, so Windows OCR reads with its English recognizer when one is installed.
        if (engine.Id == "windows" && engine.InstalledLanguages.FirstOrDefault(tag => tag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) is { } english)
            language = english;
        var availability = engine.CheckAvailability(language);
        if (!availability.IsAvailable) throw new InvalidOperationException(availability.Reason + "\n" + availability.RepairInstructions);
        double scale = double.TryParse(_settings.Ocr.Scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
        return await engine.RecognizeAsync(image, new OcrRequest(language, scale), ct);
    }

    private void Show(string query, PixelRect anchor, MonitorInfo monitor)
    {
        var state = _windows.FirstOrDefault(s => !s.Window.Pinned && !s.Window.IsClosed);
        if (state is null)
        {
            var window = new DictionaryWindow(_settings, _clipboard);
            state = new State(window);
            _windows.Add(state);
            var bound = state;
            window.Closed += (_, _) => { bound.Run?.Cancel(); _windows.Remove(bound); };
            window.LookupRequested += word => _ = RunAsync(bound, word);
        }
        state.Window.SetAnchor(anchor, monitor);
        // Runs up to its first await, so the card already has content (or a cached answer) when it appears.
        _ = RunAsync(state, query);
        state.Window.Show();
        state.Window.Activate();
        state.Window.FocusSearch();
    }

    private async Task RunAsync(State state, string query)
    {
        state.Run?.Cancel();
        using var run = new CancellationTokenSource();
        state.Run = run;
        var watch = Stopwatch.StartNew();
        try
        {
            state.Window.ShowLoading(query);
            var entry = await _service.LookupAsync(query, _settings.Dictionary.Source, run.Token);
            if (run.IsCancellationRequested || state.Window.IsClosed) return;
            state.Window.ShowEntry(entry);
            // The word itself is never written to the log.
            Log.Information("Dictionary lookup answered {Source} {Senses} senses {ElapsedMs}ms", entry.SourceId, entry.Senses.Count, watch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
        catch (DictionaryException e)
        {
            Log.Information("Dictionary lookup unanswered {Reason} {ElapsedMs}ms", e.InnerException?.GetType().Name ?? "NotFound", watch.Elapsed.TotalMilliseconds);
            if (!state.Window.IsClosed) state.Window.ShowError(query, e.Message);
        }
        catch (Exception e)
        {
            Log.Warning("Dictionary lookup failed {ErrorType}", e.GetType().Name);
            if (!state.Window.IsClosed) state.Window.ShowError(query, "查询失败，请重试。");
        }
        finally { if (ReferenceEquals(state.Run, run)) state.Run = null; }
    }

    public void Dispose() { foreach (var state in _windows.ToList()) state.Window.Close(); }
}
