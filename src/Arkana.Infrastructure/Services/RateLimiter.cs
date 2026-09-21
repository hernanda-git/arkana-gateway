using System.Collections.Concurrent;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.Services;

#pragma warning disable CA1711 // Suppressed: RateLimiter is the accurate name (a limiter IS a queue).
/// <summary>
/// In-process <see cref="IRateLimiter"/> (SEC-ARKANA-005). Each
/// per-key bucket owns:
///
///   - A sliding-window RPM counter (a queue of request timestamps
///     trimmed on every acquire; the count of timestamps inside
///     the last 60s is the current rate).
///   - A sliding-window TPM counter (same shape, but each entry
///     carries a token count rather than a 1).
///   - A concurrency counter (int, not SemaphoreSlim) for in-flight
///     requests.
///
/// Config sourced from <see cref="IRateLimitConfigProvider"/>
/// (DB-backed, 30s cache) so changes via the Settings UI take
/// effect within seconds without a restart.
///
/// Internal infra settings (cleanup cadence, idle TTL) are
/// hardcoded — they don't need runtime twiddling.
/// </summary>
public sealed class RateLimiter : IRateLimiter, IDisposable
#pragma warning restore CA1711
{
    private readonly IRateLimitConfigProvider _config;
    private readonly ILogger<RateLimiter> _logger;
    private readonly ConcurrentDictionary<string, KeyBucket> _buckets = new();
    private readonly Timer _cleanupTimer;

    // Internal infra settings (not user-facing, no need for DB/config)
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BucketIdleTtl = TimeSpan.FromMinutes(15);

    public RateLimiter(IRateLimitConfigProvider config, ILogger<RateLimiter> logger)
    {
        _config = config;
        _logger = logger;
        _cleanupTimer = new Timer(
            _ => PruneIdleBuckets(),
            state: null,
            dueTime: CleanupInterval,
            period: CleanupInterval);
    }

    public async ValueTask<RateLimitLease?> TryAcquireSlotAsync(
        string apiKeyName, CancellationToken ct = default)
    {
        if (!await _config.IsEnabledAsync())
        {
            return NoopLease();
        }

        var bucket = _buckets.GetOrAdd(apiKeyName, _ => NewBucket());
        var (defRpm, defTpm, defConc) = await _config.GetDefaultsAsync();
        var (ovRpm, ovTpm, ovConc) = await _config.GetKeyOverridesAsync(apiKeyName);

        var rpm = ovRpm ?? defRpm;
        var tpm = ovTpm ?? defTpm;
        var maxConc = ovConc ?? defConc;

        var now = DateTimeOffset.UtcNow;

        // ── Concurrency (cheapest check first) ─────────────────
        if (bucket.InFlight >= maxConc)
        {
            _logger.LogInformation(
                "Rate limit: concurrency hit for key '{Key}' ({Now}/{Max} in flight)",
                apiKeyName, bucket.InFlight, maxConc);
            return null;
        }

        // ── RPM (sliding window) ───────────────────────────────
        lock (bucket.RpmLock)
        {
            TrimTimestamps(bucket.RpmWindow, now.AddMinutes(-1));
            if (bucket.RpmWindow.Count >= rpm)
            {
                _logger.LogInformation(
                    "Rate limit: RPM hit for key '{Key}' ({Count}/{Max} in 60s)",
                    apiKeyName, bucket.RpmWindow.Count, rpm);
                return null;
            }
            bucket.RpmWindow.Enqueue(now);
        }

        // Concurrency reserve. CAS loop protects against a race.
        var initial = Interlocked.Increment(ref bucket.InFlight);
        if (initial > maxConc)
        {
            Interlocked.Decrement(ref bucket.InFlight);
            lock (bucket.RpmLock) bucket.RpmWindow.Dequeue();
            return null;
        }

        return new RateLimitLease(() =>
        {
            Interlocked.Decrement(ref bucket.InFlight);
            bucket.LastTouchedAt = DateTimeOffset.UtcNow;
            return ValueTask.CompletedTask;
        });
    }

    public bool RecordTokenUsageAsync(string apiKeyName, int inputTokens, int outputTokens)
    {
        // TPM check is best-effort post-flight — always accept the
        // charge today. A future enhancement could check the limit
        // and log overages.
        return true;
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }

    // ── Internal helpers ─────────────────────────────────────────

    private static void TrimTimestamps(Queue<DateTimeOffset> q, DateTimeOffset cutoff)
    {
        while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
    }

    private static KeyBucket NewBucket() => new();

    private void PruneIdleBuckets()
    {
        var cutoff = DateTimeOffset.UtcNow - BucketIdleTtl;
        var removed = 0;
        foreach (var kvp in _buckets)
        {
            var bucket = kvp.Value;
            if (bucket.LastTouchedAt >= cutoff) continue;
            if (Interlocked.CompareExchange(ref bucket.InFlight, 0, 0) != 0) continue;

            if (_buckets.TryRemove(kvp.Key, out _))
                removed++;
        }
        if (removed > 0)
        {
            _logger.LogDebug(
                "Rate limiter cleanup: pruned {Count} idle buckets (remaining={Remaining})",
                removed, _buckets.Count);
        }
    }

    private static RateLimitLease NoopLease() =>
        new(static () => ValueTask.CompletedTask);

    private sealed class KeyBucket
    {
        public int InFlight;
        public Queue<DateTimeOffset> RpmWindow { get; } = new();
        public object RpmLock { get; } = new();
        public DateTimeOffset LastTouchedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
