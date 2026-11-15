using System.Text;

namespace SnapTrans.Core.Imaging;

/// <summary>Deterministic Unicode-aware wrapping with CJK line-start/line-end prohibitions.</summary>
public static class LineBreaker
{
    private const string NoStart = "，。、；：？！）」』】》〉…,.;:!?)]}％%";
    private const string NoEnd = "（「『【《〈([{“‘";

    public static IReadOnlyList<string> Break(string text, float maxWidth, Func<string, float> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (!(maxWidth > 0) || !float.IsFinite(maxWidth)) throw new ArgumentOutOfRangeException(nameof(maxWidth));
        var result = new List<string>();
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            string line = "";
            foreach (string token in Tokenize(paragraph))
            {
                string remaining = token;
                if (line.Length == 0) remaining = remaining.TrimStart();
                if (remaining.Length == 0) continue;
                if (measure(line + remaining) <= maxWidth)
                {
                    line += remaining;
                    continue;
                }
                if (line.Length > 0)
                {
                    // Move the last character together with punctuation to the next line.
                    if (StartsForbidden(remaining) || EndsForbidden(line))
                    {
                        int cut = LastRuneIndex(line.TrimEnd());
                        string moved = line[cut..];
                        line = line[..cut];
                        remaining = moved + remaining;
                    }
                    if (line.Trim().Length > 0) result.Add(line.TrimEnd());
                    line = "";
                }
                remaining = remaining.TrimStart();
                if (measure(remaining) <= maxWidth) { line = remaining; continue; }
                // Last resort: an indivisible Latin token is wider than the complete line.
                foreach (var rune in remaining.EnumerateRunes())
                {
                    string c = rune.ToString();
                    if (line.Length > 0 && measure(line + c) > maxWidth && !StartsForbidden(c) && !EndsForbidden(line))
                    {
                        result.Add(line.TrimEnd());
                        line = "";
                    }
                    line += c;
                }
            }
            result.Add(line.TrimEnd());
        }
        return result;
    }

    public static bool IsCjk(int value) => value is >= 0x2e80 and <= 0x9fff or >= 0xf900 and <= 0xfaff or
        >= 0x3040 and <= 0x30ff or >= 0xac00 and <= 0xd7af or >= 0xff00 and <= 0xffef or >= 0x20000 and <= 0x323af;

    private static IEnumerable<string> Tokenize(string paragraph)
    {
        var word = new StringBuilder();
        foreach (var rune in paragraph.EnumerateRunes())
        {
            bool latinWord = !IsCjk(rune.Value) && (Rune.IsLetterOrDigit(rune) || rune.Value is '_' or '\'');
            if (latinWord) { word.Append(rune); continue; }
            if (rune.Value == '-' && word.Length > 0) { word.Append('-'); yield return word.ToString(); word.Clear(); continue; }
            if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
            yield return rune.ToString();
        }
        if (word.Length > 0) yield return word.ToString();
    }
    private static int LastRuneIndex(string value) => value.Length == 0 ? 0 : value.Length - (char.IsLowSurrogate(value[^1]) && value.Length > 1 ? 2 : 1);
    private static bool StartsForbidden(string s) => s.Length > 0 && NoStart.Contains(s[0]);
    private static bool EndsForbidden(string s) => s.Length > 0 && NoEnd.Contains(s[^1]);
}
