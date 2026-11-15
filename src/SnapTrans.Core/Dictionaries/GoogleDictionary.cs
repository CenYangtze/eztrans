using System.Text.Json.Nodes;

namespace SnapTrans.Core.Dictionaries;

/// <summary>Google Translate's keyless web endpoint. It is rate limited per address, so it serves as the second source.</summary>
public sealed class GoogleDictionary(Func<HttpClient> createClient) : IDictionaryProvider
{
    private const int MaxSenses = 8, MaxTermsPerSense = 6;
    public string Id => DictionarySources.Google;
    public string Name => "Google 翻译";

    public async Task<DictionaryEntry?> LookupAsync(string query, CancellationToken ct)
    {
        var uri = new Uri("https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=zh-CN&dt=t&dt=bd&q=" + Uri.EscapeDataString(query));
        return Parse(await DictionaryHttp.GetStringAsync(createClient, uri, ct).ConfigureAwait(false), query);
    }

    /// <summary>The answer is positional: [0] holds translated sentences, [1] per part of speech word lists.</summary>
    public static DictionaryEntry? Parse(string json, string query)
    {
        if (JsonNode.Parse(json) is not JsonArray root || root.Count == 0) return null;
        var senses = new List<DictionarySense>();
        if (root.Count > 1)
            foreach (var group in Json.Items(root[1]).OfType<JsonArray>())
            {
                if (senses.Count >= MaxSenses || group.Count < 2) break;
                var terms = Json.Items(group[1]).Select(Json.Text).OfType<string>().Take(MaxTermsPerSense).ToArray();
                if (terms.Length > 0) senses.Add(new(Abbreviate(Json.Text(group[0])), string.Join("；", terms)));
            }
        if (senses.Count == 0)
        {
            string translation = string.Concat(Json.Items(root[0]).OfType<JsonArray>().Select(part => part.Count > 0 ? Json.Text(part[0]) : null)).Trim();
            // An unknown word comes back unchanged; that is not a definition.
            if (translation.Length == 0 || translation.Equals(query, StringComparison.OrdinalIgnoreCase)) return null;
            senses.Add(new(null, translation));
        }
        return new DictionaryEntry(query, DictionarySources.Google, "Google 翻译") { Senses = senses };
    }

    private static string? Abbreviate(string? partOfSpeech) => partOfSpeech?.ToLowerInvariant() switch
    {
        null or "" => null,
        "noun" => "n.", "verb" => "v.", "adjective" => "adj.", "adverb" => "adv.", "pronoun" => "pron.",
        "preposition" => "prep.", "conjunction" => "conj.", "interjection" => "int.", "abbreviation" => "abbr.",
        "article" => "art.", "auxiliary verb" => "aux.", "prefix" => "pref.", "suffix" => "suf.", "phrase" => "phr.",
        var other => other
    };
}
