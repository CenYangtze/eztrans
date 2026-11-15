using SnapTrans.Core.Settings;

namespace SnapTrans.Core.Caching;

public interface ITranslationCache
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string text, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public sealed record TranslationCacheStatistics(int Count, long Hits, long Misses);

/// <summary>Bounded, process-local LRU cache. No screenshots, credentials or cache data are written to disk.</summary>
public sealed class MemoryTranslationCache : ITranslationCache
{
    private sealed record Entry(string Text, DateTimeOffset ExpiresAt, LinkedListNode<string> Node);
    private readonly Func<CacheSettings> _settings;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private long _hits;
    private long _misses;

    public MemoryTranslationCache(CacheSettings? settings = null, TimeProvider? timeProvider = null)
        : this(() => settings ?? new CacheSettings(), timeProvider) { }
    public MemoryTranslationCache(Func<CacheSettings> settings, TimeProvider? timeProvider = null)
    { _settings = settings ?? throw new ArgumentNullException(nameof(settings)); _time = timeProvider ?? TimeProvider.System; }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key); ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            var settings = _settings();
            Prune(settings);
            if (!settings.Enabled || !_entries.TryGetValue(key, out var entry)) { _misses++; return Task.FromResult<string?>(null); }
            _lru.Remove(entry.Node); _lru.AddFirst(entry.Node); _hits++;
            return Task.FromResult<string?>(entry.Text);
        }
    }
    public Task SetAsync(string key, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(text); ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            var settings = _settings(); Prune(settings);
            // Avoid retaining exceptionally large model outputs in an otherwise small cache.
            if (!settings.Enabled || text.Length > 1_000_000) return Task.CompletedTask;
            Remove(key);
            var node = _lru.AddFirst(key);
            _entries.Add(key, new(text, _time.GetUtcNow().AddMinutes(Math.Clamp(settings.TtlMinutes, 1, 1440)), node));
            Trim(Math.Clamp(settings.MaxEntries, 1, 10000));
        }
        return Task.CompletedTask;
    }
    public Task ClearAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) { ct.ThrowIfCancellationRequested(); _entries.Clear(); _lru.Clear(); _hits = 0; _misses = 0; }
        return Task.CompletedTask;
    }
    public TranslationCacheStatistics GetStatistics()
    {
        lock (_gate) { Prune(_settings()); return new(_entries.Count, _hits, _misses); }
    }
    private void Prune(CacheSettings settings)
    {
        if (!settings.Enabled) { _entries.Clear(); _lru.Clear(); return; }
        var now = _time.GetUtcNow();
        foreach (var expired in _entries.Where(e => e.Value.ExpiresAt <= now).Select(e => e.Key).ToArray()) Remove(expired);
        Trim(Math.Clamp(settings.MaxEntries, 1, 10000));
    }
    private void Trim(int capacity) { while (_entries.Count > capacity && _lru.Last is { } node) Remove(node.Value); }
    private void Remove(string key) { if (_entries.Remove(key, out var entry)) _lru.Remove(entry.Node); }
}
