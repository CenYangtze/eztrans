using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SnapTrans.Core.Dictionaries;

/// <summary>Youdao's public dictionary JSON endpoint; no account or key is involved.</summary>
public sealed partial class YoudaoDictionary(Func<HttpClient> createClient) : IDictionaryProvider
{
    // Only the sections the window shows are requested; the full answer is more than ten times larger.
    private static readonly string Sections = Uri.EscapeDataString("""{"count":99,"dicts":[["ec","phrs","blng_sents_part","fanyi","simple"]]}""");
    private const int MaxSenses = 8, MaxPhrases = 4, MaxExamples = 2, MaxExampleLength = 140;

    public string Id => DictionarySources.Youdao;
    public string Name => "有道词典";

    public async Task<DictionaryEntry?> LookupAsync(string query, CancellationToken ct)
    {
        var uri = new Uri($"https://dict.youdao.com/jsonapi?jsonversion=2&client=mobile&q={Uri.EscapeDataString(query)}&dicts={Sections}");
        return Parse(await DictionaryHttp.GetStringAsync(createClient, uri, ct).ConfigureAwait(false), query);
    }

    public static DictionaryEntry? Parse(string json, string query)
    {
        if (JsonNode.Parse(json) is not JsonObject root) return null;
        var word = Json.Items(Json.Get(root, "ec", "word")).OfType<JsonObject>().FirstOrDefault();
        var senses = new List<DictionarySense>();
        if (word is not null)
            foreach (var group in Json.Items(word["trs"]))
                foreach (var translation in Json.Items(Json.Get(group, "tr")))
                    foreach (var line in Json.Items(Json.Get(translation, "l", "i")))
                        if (Json.Text(line) is { } text && senses.Count < MaxSenses) senses.Add(Sense(text));
        // Sentences and rare phrases have no dictionary entry, only a machine translation.
        if (senses.Count == 0 && Json.Text(Json.Get(root, "fanyi", "tran")) is { } machine) senses.Add(new(null, machine));
        if (senses.Count == 0) return null;

        var forms = new List<DictionaryForm>();
        if (word is not null)
        {
            if (Json.Text(word["prototype"]) is { } prototype && !prototype.Equals(query, StringComparison.OrdinalIgnoreCase)) forms.Add(new("原形", prototype));
            foreach (var item in Json.Items(word["wfs"]))
                if (Json.Text(Json.Get(item, "wf", "name")) is { } name && Json.Text(Json.Get(item, "wf", "value")) is { } value) forms.Add(new(name, value));
        }
        var phrases = new List<DictionaryPhrase>();
        foreach (var item in Json.Items(Json.Get(root, "phrs", "phrs")))
        {
            if (phrases.Count >= MaxPhrases) break;
            string? head = Json.Text(Json.Get(item, "phr", "headword", "l", "i"));
            string? meaning = Json.Items(Json.Get(item, "phr", "trs")).Select(t => Json.Text(Json.Get(t, "tr", "l", "i"))).FirstOrDefault(t => t is not null);
            if (head is not null && meaning is not null) phrases.Add(new(head, meaning));
        }
        var examples = new List<DictionaryExample>();
        foreach (var item in Json.Items(Json.Get(root, "blng_sents_part", "sentence-pair")))
        {
            if (examples.Count >= MaxExamples) break;
            if (Json.Text(Json.Get(item, "sentence")) is { Length: <= MaxExampleLength } sentence && Json.Text(Json.Get(item, "sentence-translation")) is { } translated)
                examples.Add(new(sentence, translated));
        }
        return new DictionaryEntry(Json.Text(Json.Get(word, "return-phrase", "l", "i")) ?? query, DictionarySources.Youdao, "有道词典")
        {
            PhoneticUk = Json.Text(word?["ukphone"]), PhoneticUs = Json.Text(word?["usphone"]),
            Senses = senses, Forms = forms, Phrases = phrases, Examples = examples,
            Tags = Json.Items(Json.Get(root, "ec", "exam_type")).Select(Json.Text).OfType<string>().ToArray()
        };
    }

    // "n. 跑步，赛跑" and "vt. & vi. 运行" carry the part of speech inside the text.
    private static DictionarySense Sense(string text)
    {
        var match = PartOfSpeech().Match(text);
        return match.Success ? new(match.Groups["pos"].Value.Replace(" ", ""), match.Groups["text"].Value) : new(null, text);
    }

    [GeneratedRegex(@"^(?<pos>[a-z]{1,7}\.(?:\s*[&/,]\s*[a-z]{1,7}\.)*)\s*(?<text>\S.*)$", RegexOptions.Singleline)]
    private static partial Regex PartOfSpeech();
}
