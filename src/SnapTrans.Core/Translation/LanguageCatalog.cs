namespace SnapTrans.Core.Translation;

/// <summary>A selectable language: BCP 47 tag plus names used for display and search.</summary>
public sealed record LanguageOption(string Tag, string Name, string NativeName, string EnglishName)
{
    /// <summary>Text matched by search boxes: Chinese, native and English names plus the tag.</summary>
    public string SearchText { get; } = $"{Name} {NativeName} {EnglishName} {Tag}";
    public string Display => NativeName == Name ? Name : $"{Name} · {NativeName}";
}

/// <summary>Languages offered in pickers. Users never have to type a BCP 47 tag.</summary>
public static class LanguageCatalog
{
    public static IReadOnlyList<LanguageOption> All { get; } =
    [
        new("zh-Hans", "简体中文", "简体中文", "Simplified Chinese"),
        new("zh-Hant", "繁体中文", "繁體中文", "Traditional Chinese"),
        new("en", "英语", "English", "English"),
        new("ja", "日语", "日本語", "Japanese"),
        new("ko", "韩语", "한국어", "Korean"),
        new("fr", "法语", "Français", "French"),
        new("de", "德语", "Deutsch", "German"),
        new("es", "西班牙语", "Español", "Spanish"),
        new("pt", "葡萄牙语", "Português", "Portuguese"),
        new("pt-BR", "葡萄牙语（巴西）", "Português (Brasil)", "Brazilian Portuguese"),
        new("it", "意大利语", "Italiano", "Italian"),
        new("ru", "俄语", "Русский", "Russian"),
        new("uk", "乌克兰语", "Українська", "Ukrainian"),
        new("pl", "波兰语", "Polski", "Polish"),
        new("nl", "荷兰语", "Nederlands", "Dutch"),
        new("sv", "瑞典语", "Svenska", "Swedish"),
        new("da", "丹麦语", "Dansk", "Danish"),
        new("nb", "挪威语", "Norsk", "Norwegian"),
        new("fi", "芬兰语", "Suomi", "Finnish"),
        new("cs", "捷克语", "Čeština", "Czech"),
        new("sk", "斯洛伐克语", "Slovenčina", "Slovak"),
        new("hu", "匈牙利语", "Magyar", "Hungarian"),
        new("ro", "罗马尼亚语", "Română", "Romanian"),
        new("bg", "保加利亚语", "Български", "Bulgarian"),
        new("el", "希腊语", "Ελληνικά", "Greek"),
        new("tr", "土耳其语", "Türkçe", "Turkish"),
        new("ar", "阿拉伯语", "العربية", "Arabic"),
        new("he", "希伯来语", "עברית", "Hebrew"),
        new("fa", "波斯语", "فارسی", "Persian"),
        new("hi", "印地语", "हिन्दी", "Hindi"),
        new("bn", "孟加拉语", "বাংলা", "Bengali"),
        new("th", "泰语", "ไทย", "Thai"),
        new("vi", "越南语", "Tiếng Việt", "Vietnamese"),
        new("id", "印度尼西亚语", "Bahasa Indonesia", "Indonesian"),
        new("ms", "马来语", "Bahasa Melayu", "Malay"),
        new("fil", "菲律宾语", "Filipino", "Filipino"),
        new("ca", "加泰罗尼亚语", "Català", "Catalan"),
        new("hr", "克罗地亚语", "Hrvatski", "Croatian"),
        new("sr", "塞尔维亚语", "Српски", "Serbian"),
        new("sl", "斯洛文尼亚语", "Slovenščina", "Slovenian"),
        new("lt", "立陶宛语", "Lietuvių", "Lithuanian"),
        new("lv", "拉脱维亚语", "Latviešu", "Latvian"),
        new("et", "爱沙尼亚语", "Eesti", "Estonian"),
        new("la", "拉丁语", "Latina", "Latin")
    ];

    public static LanguageOption? Find(string? tag) =>
        string.IsNullOrWhiteSpace(tag) ? null : All.FirstOrDefault(l => l.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Catalog entry for the tag, or a stand-in so a hand-edited tag stays selectable.</summary>
    public static LanguageOption FindOrCustom(string tag) => Find(tag) ?? new(tag, tag, tag, tag);

    /// <summary>Readable name for any tag, including regional OCR tags such as en-US or zh-TW.</summary>
    public static string Describe(string tag)
    {
        if (Find(tag) is { } exact) return exact.Name;
        string normalized = tag.ToLowerInvariant();
        if (normalized is "zh-cn" or "zh-sg" or "zh") return "简体中文";
        if (normalized is "zh-tw" or "zh-hk" or "zh-mo") return "繁体中文";
        return Find(tag.Split('-')[0]) is { } parent ? $"{parent.Name}（{tag}）" : tag;
    }
}
