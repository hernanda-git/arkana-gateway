using System.Diagnostics.Metrics;
using Arkana.Infrastructure.Services;

namespace Arkana.Infrastructure.Observability;

/// <summary>
/// Bridges <see cref="InMemoryResponseCache"/> counters to OpenTelemetry
/// via the <c>System.Diagnostics.Metrics</c> source that the hosting
/// layer's <c>AddOpenTelemetry().WithMetrics(...)</c> is already
/// listening on (we explicitly add the source to the OTel meter
/// pipeline — see <c>ServiceDefaults</c> registration).
///
/// Why a custom MeterSource instead of a public IMetricRecorder?
///   - The .NET metrics API is the canonical way to expose metrics in
///     .NET 8+; using it gives us free integration with whatever
///     exporter Aspire (Prometheus, OTLP) wires up.
///   - The cache is one of several places that will publish metrics
///     in the future (cooldown tracker, fallback executor, …). One
///     dedicated Meter with a clear name keeps the namespace tidy.
/// </summary>
public sealed class CacheMetricsExporter : IDisposable
{
    /// <summary>
    /// Name of the <see cref="Meter"/> hosting gateway-domain metrics.
    /// Registered with the OTel meter pipeline in ServiceDefaults.
    /// </summary>
    public const string MeterName = "Arkana.Cache";

    private readonly InMemoryResponseCache _cache;
    private readonly Meter _meter;
    private readonly Counter<long> _hitsCounter;
    private readonly Counter<long> _missesCounter;
    private readonly Counter<long> _writesCounter;
    private readonly Counter<long> _evictionsCounter;
    private readonly ObservableGauge<int> _sizeGauge;
    private long _lastHits;
    private long _lastMisses;
    private long _lastWrites;
    private long _lastEvictions;

    public CacheMetricsExporter(InMemoryResponseCache cache)
    {
        _cache = cache;
        _meter = new Meter(MeterName, "1.0.0");

        _hitsCounter = _meter.CreateCounter<long>(
            name: "arkana.cache.hits",
            unit: "hits",
            description: "Cache hit count for chat completion responses.");

        _missesCounter = _meter.CreateCounter<long>(
            name: "arkana.cache.misses",
            unit: "misses",
            description: "Cache miss count for chat completion responses (key not present or expired).");

        _writesCounter = _meter.CreateCounter<long>(
            name: "arkana.cache.writes",
            unit: "writes",
            description: "Cache write count for chat completion responses.");

        _evictionsCounter = _meter.CreateCounter<long>(
            name: "arkana.cache.evictions",
            unit: "evictions",
            description: "Entries dropped by cap-based eviction. Excludes lazy-expired entries.");

        // Observable gauge for the current live size. Polled on each
        // metrics scrape — cheap O(1) read of the dictionary's count.
        _sizeGauge = _meter.CreateObservableGauge(
            name: "arkana.cache.size",
            observeValue: () => _cache.Count,
            unit: "entries",
            description: "Current number of live entries in the response cache.");
    }

    /// <summary>
    /// Convert monotonic counter increments into deltas and emit them
    /// to the OTel pipeline. Safe to call from a background loop; the
    /// "last seen" snapshot is local to this exporter instance.
    /// </summary>
    public void Flush()
    {
        var hits = _cache.Hits;
        var misses = _cache.Misses;
        var writes = _cache.Writes;
        var evictions = _cache.Evictions;

        var dHits = hits - _lastHits;
        var dMisses = misses - _lastMisses;
        var dWrites = writes - _lastWrites;
        var dEvictions = evictions - _lastEvictions;

        if (dHits > 0) _hitsCounter.Add(dHits);
        if (dMisses > 0) _missesCounter.Add(dMisses);
        if (dWrites > 0) _writesCounter.Add(dWrites);
        if (dEvictions > 0) _evictionsCounter.Add(dEvictions);

        _lastHits = hits;
        _lastMisses = misses;
        _lastWrites = writes;
        _lastEvictions = evictions;
    }

    public void Dispose() => _meter.Dispose();
}
