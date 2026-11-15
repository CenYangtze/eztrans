using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using Serilog;
using SkiaSharp;
using SnapTrans.App.Services;
using SnapTrans.App.Windows;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Imaging;
using SnapTrans.Core.Ocr;
using SnapTrans.Core.Settings;
using SnapTrans.Core.Translation;
using SnapTrans.Core.Caching;
using SnapTrans.Core.Data;
using SnapTrans.Core.ImageEditing;
using SnapTrans.Platform.Windows;

namespace SnapTrans.App.Flows;

public sealed partial class ImageTranslateFlow : IDisposable
{
    private readonly AppSettings _settings;
    private readonly IOcrEngine _ocr;
    private readonly IClipboardService _clipboard;
    private readonly ISecretProtector _secrets;
    private readonly Func<Profile, bool> _consent;
    private readonly ITranslationCache _cache;
    private readonly HistoryStore _history;
    private readonly List<ImageResultWindow> _windows = [];
    public ImageTranslateFlow(AppSettings settings, IOcrEngine ocr, IClipboardService clipboard, ISecretProtector secrets, Func<Profile, bool> consent, ITranslationCache cache, HistoryStore history)
    { _settings = settings; _ocr = ocr; _clipboard = clipboard; _secrets = secrets; _consent = consent; _cache = cache; _history = history; }

    public async Task ShowAsync(CaptureSession session, bool ownsSession, bool? forceAi = null, string? targetLanguage = null)
    {
        var windowOwner = ownsSession ? null : session.RetainOwner();
        var windowLease = session.Acquire();
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token);
        ImageResultWindow window;
        try { window = new ImageResultWindow(session.RegionVirtual, session.Image, _clipboard, session.Monitor, _settings); }
        catch { lifetime.Dispose(); windowLease.Dispose(); windowOwner?.Dispose(); throw; }
        int released = 0;
        _windows.Add(window);
        window.Closed += (_, _) =>
        {
            ReleaseWindow();
        };
        void ReleaseWindow()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            lifetime.Cancel();
            _windows.Remove(window);
            if (ownsSession) session.Dispose();
            windowOwner?.Dispose();
            windowLease.Dispose();
            lifetime.Dispose();
            Log.Information("Session {SessionId} image window closed; request cancelled", session.Id);
        }
        // The CTS must live as long as the window, including after the initial render completes.
        CancellationTokenSource? active = null;
        window.RetryRequested += async (_, _) => await RunAsync();
        window.AiImageRequested += async (_, _) => { forceAi = true; await RunAsync(); };
        try { window.Show(); }
        catch { ReleaseWindow(); throw; }
        await RunAsync();

        async Task RunAsync()
        {
            if (lifetime.IsCancellationRequested) return;
            active?.Cancel();
            using var run = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            active = run;
            using var lease = session.Acquire();
            var ct = run.Token;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                window.BeginProcessing();
                var settings = SettingsSnapshot.Clone(_settings);
                // AI redraw needs a profile that can generate images; the saved engine choice quietly
                // falls back to the standard pipeline when that profile is no longer available.
                var aiProfile = forceAi ?? settings.ImageTranslate.Engine == "ai" ? ImageGeneration.Resolve(settings) : null;
                if (forceAi == true && aiProfile is null) throw new InvalidOperationException("没有可用于 AI 重绘的模型。请在设置 → 模型中开启“图像生成”。");
                bool useAi = aiProfile is not null;
                var profile = settings.Profiles.FirstOrDefault(p => p.Id == (settings.Translation.ImageProfileId ?? settings.Translation.ActiveProfileId))
                    ?? throw new InvalidOperationException("请先在设置 → 模型中添加并选择模型。");
                if (string.IsNullOrWhiteSpace(profile.Model)) throw new InvalidOperationException("请先填写模型名并测试连接。");
                if (!_consent(profile)) { window.SetStatus("已取消发送", true); return; }
                window.SetStatus("识别文字…");
                var request = new OcrRequest(settings.Ocr.Language == "auto" ? null : settings.Ocr.Language,
                    double.TryParse(settings.Ocr.Scale, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) ? scale : 0);
                var ocrEngine = _ocr is OcrEngineRegistry registry ? registry.Snapshot() : _ocr;
                var availability = ocrEngine.CheckAvailability(request.LanguageTag);
                if (!availability.IsAvailable) throw new InvalidOperationException(availability.Reason + "\n" + availability.RepairInstructions);
                var page = await session.GetOcrAsync(ocrEngine, request).WaitAsync(ct);
                using var connection = new ModelConnection(profile, settings.Network, _secrets, _cache);
                var options = new ImageTranslateOptions
                {
                    Translation = new TranslateOptions(targetLanguage ?? settings.Translation.TargetLanguage, settings.Translation.SecondaryLanguage, profile,
                        settings.Translation.Glossary, settings.Translation.SmartSwap, settings.Translation.Prompts),
                    AttachContextImage = settings.Translation.AttachImageInImageMode && profile.SupportsVision,
                    MinFontPx = settings.ImageTranslate.MinFontPx,
                    FontMap = settings.ImageTranslate.FontMap,
                    EraseMode = settings.ImageTranslate.EraseMode,
                    DebugDump = settings.ImageTranslate.DebugDump,
                    DebugFolder = AppPaths.DebugFolder,
                    Rendering = new RenderOptions { EnableInpaint = settings.ImageTranslate.EnableInpaint, PreserveBold = settings.ImageTranslate.PreserveBold, PreserveOutline = settings.ImageTranslate.PreserveOutline },
                    // Show finished batches while later requests are still running.
                    Preview = (bitmap, completed, total) =>
                    {
                        if (ct.IsCancellationRequested) return;
                        var frame = BitmapInterop.ToBitmapSource(bitmap);
                        window.Dispatcher.BeginInvoke(() => { if (!ct.IsCancellationRequested && ReferenceEquals(active, run)) window.ShowPreview(frame, completed, total); });
                    }
                };
                var progress = new Progress<PipelineStage>(stage =>
                {
                    if (ct.IsCancellationRequested || !ReferenceEquals(active, run)) return;
                    window.SetStatus(stage switch { PipelineStage.Ocr => "识别文字…", PipelineStage.Layout => "分析版面…", PipelineStage.Translate => "翻译中…", PipelineStage.Render => "排版与合成…", _ => "译图已生成" });
                });
                if (aiProfile is not null && !_consent(aiProfile)) { window.SetStatus("已取消 AI 图片发送。", true); return; }
                using var result = await Task.Run(async () =>
                {
                    var translator = new SegmentTranslator(connection.Client);
                    if (aiProfile is null) return await new ImageTranslationPipeline(translator).RunAsync(session.Image, page, options, progress, ct);
                    using var http = HttpTransport.Create(settings.Network);
                    string key = string.IsNullOrEmpty(aiProfile.ApiKeyProtected) ? "" : _secrets.Unprotect(aiProfile.ApiKeyProtected);
                    var editor = ImageEditClientFactory.Create(settings.ImageTranslate.AiProvider, http, aiProfile, key,
                        m => Log.Information("Image HTTP {Method} {Host} {StatusCode} {ElapsedMs}ms", m.Method, m.Host, m.StatusCode, m.ElapsedMilliseconds));
                    return await new AiImageTranslationPipeline(translator, editor).RunAsync(session.Image, page, options, progress, ct);
                }, ct);
                ct.ThrowIfCancellationRequested();
                window.ShowResult(result.Output);
                window.SetAiGenerated(result.IsAiGenerated);
                window.SetStatus(result.Warnings.Count == 0 ? $"{profile.Model} · {watch.Elapsed.TotalSeconds:F1} s" : string.Join("；", result.Warnings));
                string? exportWarning = null;
                if (settings.ImageTranslate.AutoCopy)
                {
                    try { await _clipboard.SetImageAsync(result.Output, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception e) { Log.Warning("Image clipboard failed {ErrorType}", e.GetType().Name); exportWarning = "译图已生成，但自动复制失败，请手动重试。"; }
                }
                ct.ThrowIfCancellationRequested();
                if (settings.ImageTranslate.AutoSave)
                {
                    try
                    {
                    var folder = Environment.ExpandEnvironmentVariables(settings.ImageTranslate.SaveFolder);
                    Directory.CreateDirectory(folder);
                    string filename = FileNameTemplate().Replace(settings.ImageTranslate.FileNamePattern,
                        m => DateTime.Now.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture));
                    foreach (char invalid in Path.GetInvalidFileNameChars()) filename = filename.Replace(invalid, '_');
                    filename = Path.GetFileName(filename);
                    if (string.IsNullOrWhiteSpace(filename)) throw new InvalidOperationException("译图文件名模板为空。");
                    if (!filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) filename += ".png";
                    string target = Path.Combine(folder, filename);
                    // Never overwrite a user's previous capture with the same second-level timestamp.
                    if (File.Exists(target)) target = Path.Combine(folder, Path.GetFileNameWithoutExtension(filename) + "_" + Guid.NewGuid().ToString("N")[..6] + ".png");
                    byte[] png = await Task.Run(() => ImageComposer.EncodePng(result.Output), ct);
                    string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(temporary, png, ct);
                        ct.ThrowIfCancellationRequested();
                        File.Move(temporary, target);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception e) { Log.Warning("Image auto-save failed {ErrorType}", e.GetType().Name); exportWarning = "译图已生成，但自动保存失败，请手动另存为。"; }
                }
                ct.ThrowIfCancellationRequested();
                if (exportWarning is not null) window.SetStatus(exportWarning, true);
                Log.Information("Session {SessionId} image completed {ElapsedMs}ms", session.Id, watch.Elapsed.TotalMilliseconds);
                try
                {
                    await _history.RecordAsync(new(Guid.NewGuid(), DateTimeOffset.Now, useAi ? "AI 译图" : "译图", profile.Name, profile.Model,
                        options.Translation.TargetLanguage, string.Join(Environment.NewLine, result.Blocks.Select(b => b.Source)),
                        string.Join(Environment.NewLine, result.Blocks.Select(b => b.Translation ?? b.Source))), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) { Log.Warning("History write failed {ErrorType}", e.GetType().Name); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { Log.Information("Session {SessionId} image request cancelled", session.Id); }
            catch (Exception e)
            {
                Log.Warning("Session {SessionId} image failed {ErrorType}", session.Id, e.GetType().Name);
                if (!ct.IsCancellationRequested) window.SetStatus(FloatingTranslateFlow.UserError(e), true);
            }
            finally { if (ReferenceEquals(active, run)) active = null; }
        }
    }
    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex FileNameTemplate();
    public void Dispose() { foreach (var window in _windows.ToList()) window.Close(); }
}
