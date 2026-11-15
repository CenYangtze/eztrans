using System.Text;

namespace SnapTrans.Core.Translation;

public enum WritingScript { Han, Kana, Hangul, Latin, Cyrillic, Other }
public static class ScriptDetector
{
    public static WritingScript Classify(Rune value) => value.Value switch
    {
        >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x323af => WritingScript.Han,
        >= 0x3040 and <= 0x30ff or >= 0x31f0 and <= 0x31ff => WritingScript.Kana,
        >= 0xac00 and <= 0xd7af or >= 0x1100 and <= 0x11ff or >= 0x3130 and <= 0x318f => WritingScript.Hangul,
        >= 0x0041 and <= 0x024f => WritingScript.Latin,
        >= 0x0400 and <= 0x052f => WritingScript.Cyrillic,
        _ => WritingScript.Other
    };
    public static IReadOnlyDictionary<WritingScript, double> Detect(string text)
    {
        var letters = text.EnumerateRunes().Where(Rune.IsLetter).ToArray();
        if (letters.Length == 0) return new Dictionary<WritingScript, double>();
        return letters.GroupBy(Classify).ToDictionary(g => g.Key, g => (double)g.Count() / letters.Length);
    }
    public static bool IsCjkLanguage(string language) => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
        language.StartsWith("ja", StringComparison.OrdinalIgnoreCase) || language.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
    public static string SelectTarget(string text, string target, string secondary, bool smartSwap = true)
    {
        if (!smartSwap || target == secondary) return target;
        var scripts = Detect(text);
        var language = target.Split('-')[0].ToLowerInvariant();
        double Fraction(params WritingScript[] list) => list.Sum(s => scripts.GetValueOrDefault(s));
        var fraction = language switch
        {
            "zh" => Fraction(WritingScript.Han),
            "ja" => Fraction(WritingScript.Kana, WritingScript.Han),
            "ko" => Fraction(WritingScript.Hangul),
            "ru" or "uk" or "bg" or "be" => Fraction(WritingScript.Cyrillic),
            "en" or "fr" or "de" or "es" or "it" or "pt" or "nl" or "vi" => Fraction(WritingScript.Latin),
            _ => 0
        };
        // Han-only content cannot establish Japanese; kana is required for that inference.
        if (language == "ja" && Fraction(WritingScript.Kana) == 0) return target;
        return fraction >= .6 ? secondary : target;
    }
}
