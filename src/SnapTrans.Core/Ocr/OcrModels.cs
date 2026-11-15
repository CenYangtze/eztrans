using System.Text;
using SnapTrans.Core.Geometry;
using SkiaSharp;

namespace SnapTrans.Core.Ocr;

public interface IOcrEngine
{
    string Id { get; }
    OcrAvailability CheckAvailability(string? languageTag);
    IReadOnlyList<string> InstalledLanguages { get; }
    Task<OcrPage> RecognizeAsync(SKBitmap image, OcrRequest request, CancellationToken ct);
}
public sealed record OcrAvailability(bool IsAvailable, string? Reason = null, string? RepairInstructions = null);
public sealed record OcrRequest(string? LanguageTag = null, double Scale = 0);
public sealed record OcrPage(int Width, int Height, IReadOnlyList<OcrLine> Lines, double? TextAngleDeg, string EngineId, string? LanguageTag);
public sealed record OcrLine(string Text, PixelRectF Bounds, IReadOnlyList<OcrWord> Words);
public sealed record OcrWord(string Text, PixelRectF Bounds, float? Confidence = null);

public static class OcrText
{
    public static bool IsCjk(char c) => c is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff' or >= '\u3040' and <= '\u30ff' or >= '\uac00' and <= '\ud7af' or >= '\u3000' and <= '\u303f' or >= '\uff00' and <= '\uffef';
    public static string JoinWords(IEnumerable<string> words)
    {
        var output=new StringBuilder();
        foreach(var word in words.Where(w=>!string.IsNullOrWhiteSpace(w)).Select(w=>w.Trim()))
        {
            if(output.Length>0 && !IsCjk(output[^1]) && !IsCjk(word[0])) output.Append(' ');
            output.Append(word);
        }
        return output.ToString();
    }
    public static string JoinLines(IEnumerable<string> lines)
    {
        var output=new StringBuilder();
        foreach(var line in lines.Where(l=>!string.IsNullOrWhiteSpace(l)).Select(l=>l.Trim()))
        {
            if(output.Length>0)
            {
                if(output[^1]=='-' && char.IsLower(line[0])) output.Length--;
                else if(!IsCjk(output[^1]) || !IsCjk(line[0])) output.Append(' ');
            }
            output.Append(line);
        }
        return output.ToString();
    }
}
