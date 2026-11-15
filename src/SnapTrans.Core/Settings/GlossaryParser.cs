using SnapTrans.Core.Translation;

namespace SnapTrans.Core.Settings;

public static class GlossaryParser
{
    public const int MaximumEntries = 1000;
    public static IReadOnlyList<GlossaryEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<GlossaryEntry>();
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            int delimiter = raw.IndexOf('\t'); int delimiterLength = 1;
            if (delimiter < 0) { delimiter = raw.IndexOf("=>", StringComparison.Ordinal); delimiterLength = 2; }
            if (delimiter < 0) throw new FormatException("每行术语需用制表符或 => 分隔原词与译词。");
            entries.Add(new(raw[..delimiter].Trim(), raw[(delimiter + delimiterLength)..].Trim()));
        }
        var errors = Validate(entries);
        if (errors.Count > 0) throw new FormatException(string.Join("；", errors));
        return entries;
    }
    public static string Format(IEnumerable<GlossaryEntry> entries) => string.Join(Environment.NewLine, entries.Select(entry => $"{entry.Source}\t{entry.Target}"));
    public static IReadOnlyList<string> Validate(IEnumerable<GlossaryEntry> entries)
    {
        var errors = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int count = 0;
        foreach (var entry in entries)
        {
            count++;
            if (entry is null || string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Target)) { errors.Add("术语原词与译词不能为空"); continue; }
            if (entry.Source.Length > 1024 || entry.Target.Length > 1024) errors.Add("单条术语长度不得超过1024字符");
            if (entry.Source.IndexOfAny(['\r', '\n', '\t']) >= 0 || entry.Target.IndexOfAny(['\r', '\n', '\t']) >= 0) errors.Add("术语不能包含换行或制表符");
            if (!seen.Add(entry.Source.Trim())) errors.Add($"术语原词重复：{entry.Source}");
        }
        if (count > MaximumEntries) errors.Add($"术语表不得超过{MaximumEntries}条");
        return errors;
    }
}
