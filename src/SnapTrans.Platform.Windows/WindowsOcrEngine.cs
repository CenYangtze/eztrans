using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Ocr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using CoreOcrLine = SnapTrans.Core.Ocr.OcrLine;
using CoreOcrWord = SnapTrans.Core.Ocr.OcrWord;

namespace SnapTrans.Platform.Windows;

public sealed class WindowsOcrEngine : IOcrEngine
{
    private sealed record EngineSlot(OcrEngine Engine, SemaphoreSlim Gate);
    private readonly ConcurrentDictionary<string, EngineSlot> engines = new(StringComparer.OrdinalIgnoreCase);
    public string Id => "windows";
    public IReadOnlyList<string> InstalledLanguages => OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag).ToArray();

    public static string LanguagePackageInstructions(string? languageTag = null)
    {
        // Only an installed/valid Windows language tag is interpolated, never arbitrary shell text.
        var tag = string.IsNullOrWhiteSpace(languageTag) || languageTag == "auto" ? "zh-CN" : new Language(languageTag).LanguageTag;
        return "以管理员身份打开 Windows PowerShell（不是 PowerShell 7），运行：\n\n" +
            "Get-WindowsCapability -Online | Where-Object { $_.Name -Like 'Language.OCR*' }\n" +
            $"$Capability = Get-WindowsCapability -Online | Where-Object {{ $_.Name -Like 'Language.OCR*{tag}*' }}\n" +
            "$Capability | Add-WindowsCapability -Online\n\n安装完成后重新打开屏译。";
    }

    public OcrAvailability CheckAvailability(string? languageTag)
    {
        try
        {
            var engine = GetEngine(languageTag);
            return new(engine is not null);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or COMException)
        {
            string instructions;
            try { instructions = LanguagePackageInstructions(languageTag); }
            catch (ArgumentException) { instructions = LanguagePackageInstructions(); }
            return new(false, "所选 Windows OCR 语言不可用，请安装对应识别语言包。", instructions);
        }
    }

    public async Task<OcrPage> RecognizeAsync(SKBitmap image, OcrRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();
        var slot = GetEngine(request.LanguageTag);
        var page = await RecognizeWithEngineAsync(slot, image, request, ct).ConfigureAwait(false);
        // F-23: a Chinese recognizer's English segmentation can be weaker; only retry automatically
        // when the system already has an English recognizer and the first result is predominantly Latin.
        if (string.IsNullOrWhiteSpace(request.LanguageTag) || request.LanguageTag == "auto")
        {
            var text = string.Concat(page.Lines.Select(line => line.Text));
            int letters = text.Count(char.IsLetter);
            int latin = text.Count(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
            var english = InstalledLanguages.FirstOrDefault(tag => tag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (letters > 0 && latin >= 0.8 * letters && english is not null && !page.LanguageTag!.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                page = await RecognizeWithEngineAsync(GetEngine(english), image, request, ct).ConfigureAwait(false);
        }
        return page;
    }

    private EngineSlot GetEngine(string? languageTag)
    {
        bool automatic = string.IsNullOrWhiteSpace(languageTag) || languageTag == "auto";
        var key = automatic ? "auto" : new Language(languageTag!).LanguageTag;
        return engines.GetOrAdd(key, _ =>
        {
            var engine = automatic ? OcrEngine.TryCreateFromUserProfileLanguages() : OcrEngine.TryCreateFromLanguage(new Language(key));
            return engine is null
                ? throw new InvalidOperationException("Windows OCR 语言包未安装。\n" + LanguagePackageInstructions(languageTag))
                : new EngineSlot(engine, new SemaphoreSlim(1, 1));
        });
    }

    private async Task<OcrPage> RecognizeWithEngineAsync(EngineSlot slot, SKBitmap source, OcrRequest request, CancellationToken ct)
    {
        if (source.Width <= 0 || source.Height <= 0) throw new ArgumentException("截图为空。", nameof(source));
        var scale = request.Scale > 0 && double.IsFinite(request.Scale) ? request.Scale : Math.Max(source.Width, source.Height) < 1200 ? 2d : 1d;
        scale = Math.Min(scale, OcrEngine.MaxImageDimension / (double)Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale)), height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var image = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(image))
        using (var sourceImage = SKImage.FromBitmap(source))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawImage(sourceImage, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        var bytes = new byte[checked(width * height * 4)];
        Marshal.Copy(image.GetPixels(), bytes, 0, bytes.Length);
        for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        Array.Clear(bytes);
        await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
        OcrResult result;
        try { result = await slot.Engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false); }
        finally { slot.Gate.Release(); }
        ct.ThrowIfCancellationRequested();
        float sx = source.Width / (float)width, sy = source.Height / (float)height;
        var lines = new List<CoreOcrLine>();
        foreach (var line in result.Lines)
        {
            var words = line.Words.Select(word =>
            {
                var rect = word.BoundingRect;
                return new CoreOcrWord(word.Text, new PixelRectF((float)rect.X * sx, (float)rect.Y * sy, (float)rect.Width * sx, (float)rect.Height * sy));
            }).ToArray();
            if (words.Length == 0) continue;
            var bounds = words.Select(word => word.Bounds).Aggregate(PixelRectF.Union);
            lines.Add(new CoreOcrLine(OcrText.JoinWords(words.Select(word => word.Text)), bounds, words));
        }
        return new(source.Width, source.Height, lines.OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).ToArray(), result.TextAngle, Id, slot.Engine.RecognizerLanguage.LanguageTag);
    }
}
