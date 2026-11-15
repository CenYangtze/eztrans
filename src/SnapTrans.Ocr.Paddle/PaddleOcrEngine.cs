using RapidOcrNet;
using Microsoft.ML.OnnxRuntime;
using SkiaSharp;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Ocr;
using SnapTrans.Ocr.Paddle.Interop;
using CoreOcrLine = SnapTrans.Core.Ocr.OcrLine;
using CoreOcrWord = SnapTrans.Core.Ocr.OcrWord;

namespace SnapTrans.Ocr.Paddle;

public sealed class PaddleOcrPlugin : IOcrEnginePlugin
{
    public string Id => "paddle";
    public string Name => "PaddleOCR (ONNX / RapidOcrNet)";
    public IOcrEngine Create(string modelFolder, string modelPreset) => new PaddleOcrEngine(modelFolder, modelPreset);
}

/// <summary>Optional CPU OCR engine; model initialization and inference run off the caller's UI thread.</summary>
public sealed class PaddleOcrEngine : IOcrEngine, IDisposable
{
    private readonly string modelFolder;
    private readonly OcrModelPreset preset;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private RapidOcr? engine;
    private int active;
    private bool disposed;
    public string Id => "paddle:" + preset.Id + ":" + modelFolder;
    public IReadOnlyList<string> InstalledLanguages => preset.Languages;

    public PaddleOcrEngine(string modelFolder, string modelPreset)
    {
        // Official ORT 1.29 POSIX opt-out must precede native runtime initialization: the
        // runtime API alone still allows its process-info uploader and local device identifier.
        TelemetryEnvironment.DisableOnnxTelemetry();
        this.modelFolder = Path.GetFullPath(modelFolder);
        preset = OcrModelCatalog.Get(modelPreset);
    }

    public OcrAvailability CheckAvailability(string? languageTag)
    {
        if (!preset.SupportsLanguage(languageTag))
            return new(false, $"当前 PaddleOCR 模型不支持 {languageTag}。请选择配套的多语言模型，或改用 Windows OCR。 ");
        if (disposed) return new(false, "OCR 引擎已关闭。");
        using var client = new HttpClient();
        var manager = new OcrModelManager(client, modelFolder);
        return manager.HasAllFiles(preset.Id) ? new(true) : new(false, "PaddleOCR 模型尚未下载完整。", "请在设置 → OCR 中下载所选模型；模型与字典必须配套。 ");
    }

    public async Task<OcrPage> RecognizeAsync(SKBitmap image, OcrRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); active++; }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var availability = CheckAvailability(request.LanguageTag);
                if (!availability.IsAvailable) throw new InvalidOperationException(availability.Reason + "\n" + availability.RepairInstructions);
                if (engine is null) await InitializeAsync(linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                var options = (preset.ModelFamily == "v6" ? RapidOcrOptions.PPOCRv6 : RapidOcrOptions.Default) with { ReturnWordBox = true };
                var result = await engine!.DetectAsync(image, options, progress: null, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                var lines = new List<CoreOcrLine>();
                foreach (var block in result.TextBlocks)
                {
                    var bounds = Bounds(block.BoxPoints, image.Width, image.Height);
                    if (bounds.IsEmpty || string.IsNullOrWhiteSpace(block.Text)) continue;
                    var words = block.WordResults?.Where(word => !string.IsNullOrWhiteSpace(word.Text))
                        .Select(word => new CoreOcrWord(word.Text, Bounds(word.BoxPoints, image.Width, image.Height), word.Score)).ToArray() ?? [];
                    if (words.Length == 0) words = EstimateWords(block.Text, bounds);
                    words = JoinAdjacentWordFragments(words, block.Text);
                    lines.Add(new(OcrText.JoinWords(words.Select(word => word.Text)), bounds, words));
                }
                var rotatedAngle = result.TextBlocks.Where(block => block.BoxPoints.Length >= 2)
                    .Select(block => Math.Atan2(block.BoxPoints[1].Y - block.BoxPoints[0].Y, block.BoxPoints[1].X - block.BoxPoints[0].X) * 180 / Math.PI)
                    .Select(angle => angle > 90 ? angle - 180 : angle < -90 ? angle + 180 : angle)
                    .Where(angle => Math.Abs(angle) > 3).Select(angle => (double?)angle).FirstOrDefault();
                return new(image.Width, image.Height, lines.OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).ToArray(), rotatedAngle, Id, request.LanguageTag);
            }
            finally { gate.Release(); }
        }
        finally
        {
            lock (sync)
            {
                active--;
                if (disposed && active == 0) ReleaseNative();
            }
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        using var client = new HttpClient();
        var manager = new OcrModelManager(client, modelFolder);
        var status = await manager.GetStatusAsync(preset.Id, ct).ConfigureAwait(false);
        if (!status.IsReady) throw new InvalidOperationException(status.Message + " 请在设置 → OCR 中重新下载。 ");
        var paths = manager.GetPaths(preset.Id);
        var created = await Task.Run(() =>
        {
            // ONNX Runtime defaults to provider telemetry on supported systems; SnapTrans never opts in.
            OrtEnv.Instance().DisableTelemetryEvents();
            var instance = new RapidOcr();
            try
            {
                var models = (preset.ModelFamily == "v6" ? RapidOcrModelSet.PPOCRv6Small : RapidOcrModelSet.PPOCRv5Latin) with
                { DetModelPath = paths.Detector, ClsModelPath = paths.Classifier, RecModelPath = paths.Recognizer, KeysPath = paths.Dictionary };
                instance.InitModels(models, numThread: Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
                ct.ThrowIfCancellationRequested();
                return instance;
            }
            catch { instance.Dispose(); throw; }
        }, ct).ConfigureAwait(false);
        engine = created;
    }

    private static PixelRectF Bounds(SKPointI[] polygon, int width, int height)
    {
        if (polygon.Length == 0) return default;
        int left = Math.Clamp(polygon.Min(point => point.X), 0, width), top = Math.Clamp(polygon.Min(point => point.Y), 0, height);
        int right = Math.Clamp(polygon.Max(point => point.X), left, width), bottom = Math.Clamp(polygon.Max(point => point.Y), top, height);
        return new(left, top, right - left, bottom - top);
    }

    private static CoreOcrWord[] EstimateWords(string text, PixelRectF bounds)
    {
        // Legacy recognizers may have no word boxes. Character-width estimates are only a
        // fallback for layout splitting; the original line polygon remains authoritative.
        bool cjk = text.Any(OcrText.IsCjk);
        var pieces = cjk ? text.Where(c => !char.IsWhiteSpace(c)).Select(c => c.ToString()).ToArray()
            : text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int offset = 0;
        return pieces.Select(piece =>
        {
            int start = text.IndexOf(piece, offset, StringComparison.Ordinal);
            if (start < 0) start = offset;
            float width = bounds.Width * piece.Length / Math.Max(1, text.Length);
            var word = new CoreOcrWord(piece, new(bounds.X + bounds.Width * start / Math.Max(1, text.Length), bounds.Y, width, bounds.Height));
            offset = start + piece.Length;
            return word;
        }).ToArray();
    }

    private static CoreOcrWord[] JoinAdjacentWordFragments(IReadOnlyList<CoreOcrWord> words, string source)
    {
        var joined = new List<CoreOcrWord>();
        int offset = 0;
        foreach (var word in words)
        {
            int index = source.IndexOf(word.Text, offset, StringComparison.Ordinal);
            bool separated = index < 0 || source.AsSpan(offset, index - offset).IndexOfAny(" \t\n\r") >= 0;
            if (joined.Count > 0 && !separated && !OcrText.IsCjk(joined[^1].Text[^1]) && !OcrText.IsCjk(word.Text[0]))
            {
                var previous = joined[^1];
                joined[^1] = new(previous.Text + word.Text, PixelRectF.Union(previous.Bounds, word.Bounds),
                    previous.Confidence is float first && word.Confidence is float second ? (first + second) / 2 : null);
            }
            else joined.Add(word);
            if (index >= 0) offset = index + word.Text.Length;
        }
        return joined.ToArray();
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            if (active == 0) ReleaseNative();
        }
    }
    private void ReleaseNative() { engine?.Dispose(); engine = null; gate.Dispose(); lifetime.Dispose(); }
}
