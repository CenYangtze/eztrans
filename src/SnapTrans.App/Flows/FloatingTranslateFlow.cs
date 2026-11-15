using System.Diagnostics;
using System.Text;
using Serilog;
using SnapTrans.App.Services;
using SnapTrans.App.Windows;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Layout;
using SnapTrans.Core.Llm;
using SnapTrans.Core.Ocr;
using SnapTrans.Core.Settings;
using SnapTrans.Core.Translation;
using SnapTrans.Core.Caching;
using SnapTrans.Core.Data;
using SnapTrans.Platform.Windows;

namespace SnapTrans.App.Flows;

public sealed record ImageTranslationRequest(CaptureSession Session, bool Ai = false, string? ProfileId = null, string? TargetLanguage = null);

public sealed class FloatingTranslateFlow : IDisposable
{
    private sealed class State(FloatingResultWindow window, CaptureSession session)
    {
        public FloatingResultWindow Window { get; } = window;
        public CaptureSession Session { get; set; } = session;
        public CancellationTokenSource? Run { get; set; }
        public string? ProfileId { get; set; }
        public string? TargetLanguage { get; set; }
        public DateTimeOffset? PinnedAt { get; set; }
    }
    private readonly AppSettings _settings;
    private readonly IOcrEngine _ocr;
    private readonly IClipboardService _clipboard;
    private readonly ISecretProtector _secrets;
    private readonly Action _save;
    private readonly Func<Profile, bool> _consent;
    private readonly ITranslationCache _cache;
    private readonly HistoryStore _history;
    private readonly List<State> _windows = [];
    public event EventHandler<ImageTranslationRequest>? ImageRequested;

    public FloatingTranslateFlow(AppSettings settings, IOcrEngine ocr, IClipboardService clipboard,
        ISecretProtector secrets, Action save, Func<Profile, bool> consent, ITranslationCache cache, HistoryStore history)
    { _settings = settings; _ocr = ocr; _clipboard = clipboard; _secrets = secrets; _save = save; _consent = consent; _cache = cache; _history = history; }

    public async Task ShowAsync(CaptureSession session)
    {
        var state = _windows.FirstOrDefault(s => !s.Window.Pinned);
        if (state is not null)
        {
            state.Run?.Cancel(); state.Window.CancelStreaming(); state.Session.Dispose(); state.Session = session;
        }
        else
        {
            var window = new FloatingResultWindow(_settings, _clipboard, _save);
            state = new State(window, session);
            _windows.Add(state);
            var bound = state;
            window.Closed += (_, _) =>
            {
                bound.Run?.Cancel(); bound.Session.Dispose(); _windows.Remove(bound);
                Log.Information("Session {SessionId} closed; request cancelled", bound.Session.Id);
            };
            window.RetryRequested += async (_, _) => await RunAsync(bound);
            window.ImageRequested += (_, _) => ImageRequested?.Invoke(this, new(bound.Session, false, null, bound.TargetLanguage));
            window.AiImageRequested += (_, _) => ImageRequested?.Invoke(this, new(bound.Session, true, null, bound.TargetLanguage));
            window.RetranslationRequested += async (_, requested) =>
            {
                bound.ProfileId = requested.ProfileId; bound.TargetLanguage = requested.TargetLanguage;
                await RunAsync(bound);
            };
            window.PinnedChanged += (_, _) =>
            {
                bound.PinnedAt = window.Pinned ? DateTimeOffset.UtcNow : null;
                var pinned = _windows.Where(s => s.Window.Pinned).OrderBy(s => s.PinnedAt).ToList();
                if (pinned.Count > 10) pinned[0].Window.Close();
            };
        }
        state.ProfileId = _settings.Translation.ActiveProfileId;
        state.TargetLanguage = _settings.Translation.TargetLanguage;
        state.Window.SetTranslationContext(state.ProfileId, state.TargetLanguage);
        state.Window.SetContent(""); state.Window.SetOriginal(null);
        state.Window.SetRegion(session.RegionVirtual, session.Monitor);
        state.Window.Show(); state.Window.Activate();
        Log.Information("Session {SessionId} floating window shown", session.Id);
        await RunAsync(state);
    }

    private async Task RunAsync(State state)
    {
        var session = state.Session;
        using var lease = session.Acquire();
        state.Run?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token);
        state.Run = request;
        var ct = request.Token;
        state.Window.CancelStreaming(); state.Window.SetContent(""); state.Window.SetOriginal(null);
        var watch = Stopwatch.StartNew();
        try
        {
            var settings = SettingsSnapshot.Clone(_settings);
            var profile = settings.Profiles.FirstOrDefault(p => p.Id == (state.ProfileId ?? settings.Translation.ActiveProfileId))
                ?? throw new InvalidOperationException("请先在设置 → 模型中添加并选择默认模型。");
            if (string.IsNullOrWhiteSpace(profile.Model)) throw new InvalidOperationException("请先在设置 → 模型中填写模型名并测试连接。");
            if (!_consent(profile)) { state.Window.SetStatus("已取消发送。可随时重试。"); return; }
            using var connection = new ModelConnection(profile, settings.Network, _secrets, _cache);
            var options = new TranslateOptions(state.TargetLanguage ?? settings.Translation.TargetLanguage, settings.Translation.SecondaryLanguage,
                profile, settings.Translation.Glossary, settings.Translation.SmartSwap, settings.Translation.Prompts);
            bool vision = settings.Translation.Pipeline == "vision" || (settings.Translation.Pipeline == "auto" && profile.SupportsVision);
            string sourceText = "", finalText = "";
            bool cacheHit = false;
            try { await StreamAsync(vision); }
            catch (LlmException e) when (vision && e.VisionUnsupported && !ct.IsCancellationRequested)
            {
                Log.Information("Session {SessionId} vision unsupported; OCR fallback", session.Id);
                state.Window.SetContent(""); vision = false; await StreamAsync(false);
            }
            ct.ThrowIfCancellationRequested();
            state.Window.FinishStreaming();
            state.Window.SetStatus($"{(vision ? "Vision" : "OCR+Text")} · {profile.Model} · {watch.Elapsed.TotalSeconds:F1} s{(cacheHit ? " · 缓存" : "")}");
            Log.Information("Session {SessionId} translated {Pipeline} {ElapsedMs}ms", session.Id, vision ? "Vision" : "OCR+Text", watch.Elapsed.TotalMilliseconds);
            if (!string.IsNullOrWhiteSpace(finalText) && finalText.Trim() != "[NO_TEXT]")
            {
                try { await _history.RecordAsync(new(Guid.NewGuid(), DateTimeOffset.Now, vision ? "Vision" : "OCR+Text", profile.Name, profile.Model, options.TargetLanguage, sourceText, finalText), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) { Log.Warning("History write failed {ErrorType}", e.GetType().Name); }
            }

            async Task StreamAsync(bool useVision)
            {
                IAsyncEnumerable<LlmDelta> stream;
                if (useVision)
                {
                    state.Window.SetStatus("翻译中 · Vision");
                    stream = new VisionTranslator(connection.Client).TranslateAsync(session.Image, options, ct);
                }
                else
                {
                    state.Window.SetStatus("识别中 · Windows OCR");
                    var ocrRequest = new OcrRequest(settings.Ocr.Language == "auto" ? null : settings.Ocr.Language,
                        double.TryParse(settings.Ocr.Scale, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale) ? scale : 0);
                    var ocrEngine = _ocr is OcrEngineRegistry registry ? registry.Snapshot() : _ocr;
                    var availability = ocrEngine.CheckAvailability(ocrRequest.LanguageTag);
                    if (!availability.IsAvailable) throw new InvalidOperationException(availability.Reason + "\n" + availability.RepairInstructions);
                    var page = await session.GetOcrAsync(ocrEngine, ocrRequest).WaitAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    var blocks = new BlockBuilder().Build(page);
                    sourceText = string.Join(Environment.NewLine, blocks.Select(b => b.SourceText));
                    state.Window.SetOriginal(sourceText);
                    if (blocks.Count == 0) { state.Window.SetContent("未识别到文字"); return; }
                    state.Window.SetStatus("翻译中 · OCR+Text");
                    stream = new TextTranslator(connection.Client).TranslateAsync(blocks, options, ct);
                }
                bool first = true;
                var complete = new StringBuilder();
                await foreach (var delta in stream.WithCancellation(ct))
                {
                    ct.ThrowIfCancellationRequested();
                    if (delta is TextDelta text)
                    {
                        if (first) { Log.Information("Session {SessionId} first token {ElapsedMs}ms", session.Id, watch.Elapsed.TotalMilliseconds); first = false; }
                        complete.Append(text.Text); state.Window.AppendText(text.Text);
                    }
                    else if (delta is ReasoningDelta) state.Window.SetStatus("模型思考中…");
                    else if (delta is CacheHitDelta) cacheHit = true;
                    else if (delta is UsageDelta usage) Log.Information("Session {SessionId} tokens input={Input} output={Output}", session.Id, usage.InputTokens, usage.OutputTokens);
                }
                ct.ThrowIfCancellationRequested();
                finalText = complete.ToString();
                if (complete.ToString().Trim() == "[NO_TEXT]") state.Window.SetContent("未识别到文字");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { Log.Information("Session {SessionId} request cancelled", session.Id); }
        catch (Exception e)
        {
            Log.Warning("Session {SessionId} failed {ErrorType}", session.Id, e.GetType().Name);
            if (!ct.IsCancellationRequested) { state.Window.FinishStreaming(); state.Window.SetStatus(UserError(e), true); }
        }
        finally { if (ReferenceEquals(state.Run, request)) state.Run = null; }
    }
    private OcrRequest OcrRequest() => new(_settings.Ocr.Language == "auto" ? null : _settings.Ocr.Language,
        double.TryParse(_settings.Ocr.Scale, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale) ? scale : 0);
    internal static string UserError(Exception e) => e switch
    {
        LlmException or InvalidOperationException => e.Message,
        OperationCanceledException => "请求超时，请检查网络或增大模型超时时间。",
        System.Security.Cryptography.CryptographicException => "API Key 无法解密。请在模型设置中为当前 Windows 用户重新填写。",
        HttpRequestException => "无法连接模型服务，请检查地址、网络与代理。",
        DllNotFoundException or BadImageFormatException or TypeLoadException => "OCR 组件或其本机依赖无法加载，请重新安装与当前架构匹配的 PaddleOCR 组件。",
        _ => "处理失败，请重试；可在设置 → 高级中打开日志查看错误类型。"
    };
    public void Dispose() { foreach (var state in _windows.ToList()) state.Window.Close(); }
}
