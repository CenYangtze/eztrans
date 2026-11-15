namespace SnapTrans.Core.Dictionaries;

/// <summary>One meaning; <see cref="PartOfSpeech"/> is an abbreviation such as "n." or null when the source gives none.</summary>
public sealed record DictionarySense(string? PartOfSpeech, string Meaning);
/// <summary>An inflected form, e.g. ("过去式", "ran").</summary>
public sealed record DictionaryForm(string Name, string Value);
public sealed record DictionaryPhrase(string Text, string Meaning);
public sealed record DictionaryExample(string Text, string Translation);

/// <summary>An English → Chinese dictionary result, already reduced to what the lookup window shows.</summary>
public sealed record DictionaryEntry(string Word, string SourceId, string SourceName)
{
    public string? PhoneticUk { get; init; }
    public string? PhoneticUs { get; init; }
    public IReadOnlyList<DictionarySense> Senses { get; init; } = [];
    public IReadOnlyList<DictionaryForm> Forms { get; init; } = [];
    /// <summary>Exam or level labels such as "CET4".</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<DictionaryPhrase> Phrases { get; init; } = [];
    public IReadOnlyList<DictionaryExample> Examples { get; init; } = [];
}

public interface IDictionaryProvider
{
    string Id { get; }
    string Name { get; }
    /// <summary>Returns null when the service answered but has no entry for the query.</summary>
    Task<DictionaryEntry?> LookupAsync(string query, CancellationToken ct);
}

/// <summary>A lookup failure with a message that can be shown to the user as is.</summary>
public sealed class DictionaryException(string message, Exception? inner = null) : Exception(message, inner);

public static class DictionarySources
{
    public const string Auto = "auto", Youdao = "youdao", Google = "google";
    public static bool IsValid(string? id) => id is Auto or Youdao or Google;
    /// <summary>The page a browser can open for the same query.</summary>
    public static Uri WebPage(string source, string query) => source == Google
        ? new Uri("https://translate.google.com/?sl=en&tl=zh-CN&op=translate&text=" + Uri.EscapeDataString(query))
        : new Uri("https://dict.youdao.com/result?lang=en&word=" + Uri.EscapeDataString(query));
}
