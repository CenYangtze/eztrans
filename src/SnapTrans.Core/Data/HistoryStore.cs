using System.Text.Json;
using SnapTrans.Core.Settings;

namespace SnapTrans.Core.Data;

public sealed record HistoryEntry(Guid Id, DateTimeOffset CreatedAt, string Pipeline, string ProfileName,
    string Model, string TargetLanguage, string SourceText, string Translation);

public interface ITranslationHistory
{
    Task RecordAsync(HistoryEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<HistoryEntry>> ListAsync(CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>Explicit opt-in text history. No image data or API credentials belong in history entries.</summary>
public sealed class HistoryStore(string filePath, Func<HistorySettings> settings) : ITranslationHistory
{
    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public List<HistoryEntry> Entries { get; set; } = [];
    }
    private readonly string _filePath = Path.GetFullPath(filePath);
    private readonly Func<HistorySettings> _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const int MaximumDocumentBytes = 32 * 1024 * 1024;
    private const int MaximumTextCharacters = 100_000;

    public async Task RecordAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry); ct.ThrowIfCancellationRequested();
        if (!_settings().Enabled) return;
        if (string.IsNullOrWhiteSpace(entry.Translation)) return;
        ArgumentNullException.ThrowIfNull(entry.SourceText); ArgumentNullException.ThrowIfNull(entry.Pipeline);
        ArgumentNullException.ThrowIfNull(entry.ProfileName); ArgumentNullException.ThrowIfNull(entry.Model); ArgumentNullException.ThrowIfNull(entry.TargetLanguage);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var config = _settings();
            if (!config.Enabled) return;
            var document = await ReadCoreAsync(ct).ConfigureAwait(false);
            document.Entries.RemoveAll(existing => existing.Id == entry.Id);
            document.Entries.Insert(0, entry with { SourceText = Limit(entry.SourceText), Translation = Limit(entry.Translation),
                Pipeline = Metadata(entry.Pipeline), ProfileName = Metadata(entry.ProfileName), Model = Metadata(entry.Model), TargetLanguage = Metadata(entry.TargetLanguage) });
            int limit = Math.Clamp(config.Limit, 1, 5000);
            if (document.Entries.Count > limit) document.Entries.RemoveRange(limit, document.Entries.Count - limit);
            // Retain newest entries within a disk budget as well as a count budget.
            int budget = 0;
            int keep = 0;
            foreach (var item in document.Entries)
            {
                budget += item.SourceText.Length + item.Translation.Length + item.Pipeline.Length + item.ProfileName.Length + item.Model.Length + item.TargetLanguage.Length + 256;
                // JSON may escape a UTF-16 character into six ASCII bytes.
                if (budget > MaximumDocumentBytes / 8) break;
                keep++;
            }
            if (keep < document.Entries.Count) document.Entries.RemoveRange(keep, document.Entries.Count - keep);
            await WriteCoreAsync(document, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    // Convenient name for callers that treat history as an append-only collection.
    public Task AddAsync(HistoryEntry entry, CancellationToken ct = default) => RecordAsync(entry, ct);

    public async Task<IReadOnlyList<HistoryEntry>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await ReadCoreAsync(ct).ConfigureAwait(false);
            return document.Entries.Take(Math.Clamp(_settings().Limit, 1, 5000)).ToArray();
        }
        finally { _gate.Release(); }
    }
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(_filePath)) File.Delete(_filePath);
            if (File.Exists(_filePath + ".tmp")) File.Delete(_filePath + ".tmp");
            string folder = Path.GetDirectoryName(_filePath)!;
            string stem = Path.GetFileNameWithoutExtension(_filePath);
            if (Directory.Exists(folder)) foreach (string backup in Directory.GetFiles(folder, stem + ".bad-*.json")) File.Delete(backup);
        }
        finally { _gate.Release(); }
    }
    private async Task<Document> ReadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return new();
        try
        {
            if (new FileInfo(_filePath).Length > MaximumDocumentBytes) throw new JsonException("History document is too large.");
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = await JsonSerializer.DeserializeAsync<Document>(stream, SettingsStore.JsonOptions, ct).ConfigureAwait(false) ?? throw new JsonException("History is empty.");
            if (document.Version != 1) throw new NotSupportedException("历史记录版本不受支持，请使用较新版本屏译打开。");
            if (document.Entries is null || document.Entries.Any(entry => entry is null || entry.SourceText is null || entry.Translation is null || entry.Pipeline is null || entry.ProfileName is null || entry.Model is null || entry.TargetLanguage is null)) throw new JsonException("History contains invalid entries.");
            return document;
        }
        catch (JsonException)
        {
            string backup = Path.Combine(Path.GetDirectoryName(_filePath)!, $"{Path.GetFileNameWithoutExtension(_filePath)}.bad-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
            File.Move(_filePath, backup);
            return new();
        }
    }
    private async Task WriteCoreAsync(Document document, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string temporary = _filePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, SettingsStore.JsonOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            if (File.Exists(_filePath)) File.Replace(temporary, _filePath, null); else File.Move(temporary, _filePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Limit(string text)
    {
        if (text.Length <= MaximumTextCharacters) return text;
        int length = char.IsHighSurrogate(text[MaximumTextCharacters - 1]) ? MaximumTextCharacters - 1 : MaximumTextCharacters;
        return text[..length];
    }
    private static string Metadata(string text) => text.Length > 1024 ? text[..1024] : text;
}
