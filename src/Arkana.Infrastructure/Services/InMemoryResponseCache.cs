using System.Collections.Concurrent;
using Arkana.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// Process-local implementation of <see cref="IResponseCache"/>.
///
/// Design:
///   - One <see cref="ConcurrentDictionary{TKey, TEntry}"/> keyed by the
///     hex <see cref="CacheKey.Hash"/>. <c>GetAsync</c> is a single
///     dictionary lookup (lock-free on the read path).
///   - Each entry holds its <c>ExpiresAt</c> so expiration is checked
///     lazily on read. Expired entries are removed in a "best-effort"
///     sweep on read; they are also dropped on the next <c>SetAsync</c>
///     if the cache has exceeded the configured cap.
///   - Eviction is FIFO-oldest (first-in-first-out). A full LRU would
///     require a linked-list and an O(1) move-to-front on every read,
///     which is overkill at this scale. FIFO bounds worst-case
///     memory at <c>MaxEntries * sizeof(entry)</c> and the cap
///     <c>MaxEntries</c> is the only thing that matters for production
///     memory budget — a few hundred MB at the default cap.
///   - The cache is a singleton in DI. Concurrency is fine because
///     <c>ConcurrentDictionary</c> handles all the synchronization and
///     the eviction queue is best-effort (slightly stale eviction order
///     is not observable to callers).
///
/// Metrics:
///   - <c>Hits</c> / <c>Misses</c> / <c>Evictions</c> are exposed via
///     simple atomic counters for observability. The hosting
///     integration (<c>CacheMetricsExporter</c>) wires them into
///     OpenTelemetry's <c>System.Diagnostics.Metrics</c> pipeline.
/// </summary>
public sealed class InMemoryResponseCache : IResponseCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly LinkedList<string> _insertionOrder = new(); // FIFO queue of keys
    private readonly object _evictionLock = new();
    private readonly ILogger<InMemoryResponseCache> _logger;
    private readonly int _maxEntries;

    // Metric counters — long is 64-bit and aligned, so Interlocked
    // operations are guaranteed atomic on every supported platform.
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _writes;

    public InMemoryResponseCache(
        ILogger<InMemoryResponseCache> logger,
        InMemoryResponseCacheOptions? options = null)
    {
        _logger = logger;
        _maxEntries = (options ?? InMemoryResponseCacheOptions.Default).MaxEntries;
    }

    /// <summary>Total live (non-expired) entries. Includes the lazy-cleanup window.</summary>
    public int Count => _entries.Count;

    /// <summary>Successful <see cref="GetAsync"/> lookups since process start.</summary>
    public long Hits => System.Threading.Interlocked.Read(ref _hits);

    /// <summary>Misses (key not present or expired) since process start.</summary>
    public long Misses => System.Threading.Interlocked.Read(ref _misses);

    /// <summary>Total <see cref="SetAsync"/> calls since process start.</summary>
    public long Writes => System.Threading.Interlocked.Read(ref _writes);

    /// <summary>
    /// Entries dropped by the cap-based eviction policy. Excludes
    /// lazy-expired entries (those just disappear on read).
    /// </summary>
    public long Evictions => System.Threading.Interlocked.Read(ref _evictions);

    public Task<CachedResponse?> GetAsync(CacheKey key, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(key.Hash, out var entry))
        {
            System.Threading.Interlocked.Increment(ref _misses);
            return Task.FromResult<CachedResponse?>(null);
        }

        // Expiration check — drop and return null if past TTL.
        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            if (_entries.TryRemove(key.Hash, out _))
            {
                lock (_evictionLock) _insertionOrder.Remove(key.Hash);
            }
            System.Threading.Interlocked.Increment(ref _misses);
            return Task.FromResult<CachedResponse?>(null);
        }

        System.Threading.Interlocked.Increment(ref _hits);
        return Task.FromResult<CachedResponse?>(entry.Value);
    }

    public Task SetAsync(CacheKey key, CachedResponse value, TimeSpan ttl, CancellationToken ct = default)
    {
        if (ttl <= TimeSpan.Zero) return Task.CompletedTask;
        System.Threading.Interlocked.Increment(ref _writes);

        var expiresAt = DateTimeOffset.UtcNow + ttl;
        var entry = new Entry(value, expiresAt);

        // Insert/replace. Track insertion order for FIFO eviction.
        _entries[key.Hash] = entry;
        lock (_evictionLock)
        {
            if (_insertionOrder.Contains(key.Hash))
                _insertionOrder.Remove(key.Hash);
            _insertionOrder.AddLast(key.Hash);
        }

        // Cap-based eviction (outside the lock, best-effort).
        EvictIfOverCap();
        return Task.CompletedTask;
    }

    public void InvalidateAll()
    {
        _entries.Clear();
        lock (_evictionLock) _insertionOrder.Clear();
        _logger.LogInformation("Response cache cleared (InvalidateAll)");
    }

    private void EvictIfOverCap()
    {
        if (_entries.Count <= _maxEntries) return;

        // Best-effort: walk from the oldest end. If a single SetAsync
        // blows the cap by a lot (e.g., someone bumps the cap way down
        // at runtime), we may evict multiple entries in one call. That
        // is fine — eviction is a maintenance concern, not a correctness
        // one.
        while (_entries.Count > _maxEntries)
        {
            string? oldest;
            lock (_evictionLock)
            {
                oldest = _insertionOrder.First?.Value;
                if (oldest is not null) _insertionOrder.RemoveFirst();
            }
            if (oldest is null) break;
            if (_entries.TryRemove(oldest, out _))
            {
                System.Threading.Interlocked.Increment(ref _evictions);
            }
        }
    }

    private sealed record Entry(CachedResponse Value, DateTimeOffset ExpiresAt);
}

/// <summary>
/// Tunable options for <see cref="InMemoryResponseCache"/>.
/// </summary>
public sealed class InMemoryResponseCacheOptions
{
    /// <summary>
    /// Default: 1024 entries. Each entry is roughly a few hundred bytes
    /// of token accounting + content, so the cap bounds the cache at
    /// ~1-2 MB for typical chat completions. Bump for very high QPS
    /// workloads with stable prompt distributions.
    /// </summary>
    public static InMemoryResponseCacheOptions Default { get; } = new() { MaxEntries = 1024 };

    /// <summary>
    /// Test preset: 4 entries. Forces FIFO eviction behavior to be
    /// exercised in unit tests.
    /// </summary>
    public static InMemoryResponseCacheOptions Test { get; } = new() { MaxEntries = 4 };

    public int MaxEntries { get; init; } = 1024;
}
