using System.Globalization;
using System.Text.RegularExpressions;
using SnapTrans.Core.Settings;

namespace SnapTrans.Core.Translation;

public static partial class PromptBuilder
{
    private static readonly HashSet<string> Variables = ["target_lang", "secondary_lang", "glossary", "ocr_text"];
    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex VariableRegex();
    [GeneratedRegex(@"^[\t ]*\{\{\s*glossary\s*\}\}[\t ]*(?:\r?\n|$)", RegexOptions.Multiline)]
    private static partial Regex EmptyGlossaryLineRegex();

    public static IReadOnlyList<string> ValidateGlossary(IReadOnlyList<GlossaryEntry> glossary) => GlossaryParser.Validate(glossary);

    public static IReadOnlyList<string> Validate(string template, params string[] requiredVariables)
    {
        var names = VariableRegex().Matches(template).Select(m => m.Groups[1].Value).ToHashSet();
        var errors = requiredVariables.Where(v => !names.Contains(v)).Select(v => $"缺少必需变量：{{{{{v}}}}}").ToList();
        errors.AddRange(names.Where(v => !Variables.Contains(v)).Select(v => $"未知变量：{{{{{v}}}}}"));
        return errors;
    }

    public static string Build(string template, TranslateOptions options, string ocrText = "")
    {
        var errors = Validate(template);
        if (errors.Count != 0) throw new ArgumentException(string.Join("；", errors), nameof(template));
        var glossaryErrors = ValidateGlossary(options.Glossary);
        if (glossaryErrors.Count != 0) throw new ArgumentException(string.Join("；", glossaryErrors), nameof(options));
        var glossary = options.Glossary.Count == 0 ? "" : "Terminology (use these translations exactly):\n" +
            string.Join('\n', options.Glossary.Select(g => $"{g.Source.Trim()} => {g.Target.Trim()}"));
        var hasGlossaryVariable = VariableRegex().Matches(template).Any(m => m.Groups[1].Value == "glossary");
        if (glossary.Length == 0) template = EmptyGlossaryLineRegex().Replace(template, "");
        var values = new Dictionary<string, string>
        {
            ["target_lang"] = LanguageName(options.TargetLanguage),
            ["secondary_lang"] = LanguageName(options.SmartSwap ? options.SecondaryLanguage : options.TargetLanguage),
            ["glossary"] = glossary,
            ["ocr_text"] = ocrText
        };
        var rendered = VariableRegex().Replace(template, m => values[m.Groups[1].Value]);
        // A custom template may predate glossary support. A configured glossary remains
        // effective without requiring the user to remember adding its placeholder.
        return glossary.Length > 0 && !hasGlossaryVariable ? rendered + "\n\n" + glossary : rendered;
    }

    public static string LanguageName(string tag)
    {
        if (LanguageCatalog.Find(tag) is { } known) return $"{known.EnglishName} ({known.Tag})";
        try { return $"{CultureInfo.GetCultureInfo(tag).EnglishName} ({tag})"; }
        catch (CultureNotFoundException) { return tag; }
    }
}
