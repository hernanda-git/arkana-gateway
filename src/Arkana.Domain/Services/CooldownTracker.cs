using System.Collections.Concurrent;

namespace Arkana.Domain.Services;

/// <summary>
/// Tracks per-account failure state and short-circuits calls to a
/// failing account until a cooldown period has elapsed.
///
/// Why cooldown?
///   - A provider hitting a sustained outage (DNS down, 502/503 storm,
///     auth-key rotation in progress) will retry-and-fail in a tight
///     loop. Each failed call still incurs TCP+TLS+HTTP overhead on
///     both the gateway and the provider side, amplifying the outage.
///   - The retry+fallback layer (REL-ARKANA-001 / 002) handles the
///     "next request" case. Cooldown handles the "same account gets
///     hammered with 100 RPS for 30 seconds" case by dropping requests
///     fast (no I/O) and giving the upstream time to recover.
///
/// Scope:
///   - In-memory, single-instance. Multi-instance deployments need a
///     shared store (Redis is the natural choice — added in Phase 2).
///   - Failures recorded by the caller (e.g., FallbackChainExecutor or
///     the chat service after exhausting retries).
///   - Successes automatically clear the failure count for the account
///     (key in the same way), so a recovered provider serves traffic
///     immediately.
///
/// API:
///   - <see cref="IsOnCooldown"/> — fast pre-flight check; returns the
///     remaining cooldown if active, null otherwise.
///   - <see cref="RecordFailure"/> — call after a failure; bumps count
///     and may open the cooldown.
///   - <see cref="RecordSuccess"/> — clears the failure record.
///   - <see cref="CooldownThreshold"/> — failures within
///     <see cref="CooldownWindow"/> needed to open the cooldown.
/// </summary>
public sealed class CooldownTracker
{
    private readonly CooldownOptions _options;
    private readonly ConcurrentDictionary<string, FailureRecord> _failures = new();

    public CooldownTracker(CooldownOptions? options = null)
    {
        _options = options ?? CooldownOptions.Default;
    }

    /// <summary>Number of failures within the window that opens a cooldown.</summary>
    public int CooldownThreshold => _options.Threshold;

    /// <summary>Sliding window over which failures are counted.</summary>
    public TimeSpan CooldownWindow => _options.Window;

    /// <summary>How long the cooldown stays open once triggered.</summary>
    public TimeSpan CooldownDuration => _options.Duration;

    /// <summary>
    /// Check whether the given account is currently on cooldown.
    /// Returns the remaining cooldown duration, or null if not on cooldown.
    /// </summary>
    /// <remarks>
    /// Does NOT clear expired cooldowns — that's done lazily on the next
    /// <see cref="RecordFailure"/> or <see cref="RecordSuccess"/> call. This
    /// keeps the hot path allocation-free.
    /// </remarks>
    public TimeSpan? IsOnCooldown(string accountKey)
    {
        if (string.IsNullOrEmpty(accountKey)) return null;
        if (!_failures.TryGetValue(accountKey, out var record)) return null;
        if (record.CooldownUntil is null) return null;

        var remaining = record.CooldownUntil.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            // Cooldown expired — clear it so a subsequent call can succeed
            // without first hitting a failure.
            _failures.TryRemove(accountKey, out _);
            return null;
        }
        return remaining;
    }

    /// <summary>
    /// Record a failure for the account. Opens a cooldown if the failure
    /// count within the configured window exceeds the threshold.
    /// </summary>
    public void RecordFailure(string accountKey)
    {
        if (string.IsNullOrEmpty(accountKey)) return;
        var now = DateTimeOffset.UtcNow;
        var duration = _options.Duration;
        var threshold = _options.Threshold;

        _failures.AddOrUpdate(accountKey,
            // New key — create a record with this single failure.
            // If the threshold is 1, the first failure alone opens the
            // cooldown (e.g., "fail once and we go to cooldown").
            _ => BuildRecord(1, now, now, openCooldown: threshold <= 1, duration),
            // Existing key — slide the window
            (_, existing) =>
            {
                // If outside the window, reset the count
                if (now - existing.FirstFailureAt > _options.Window)
                {
                    return BuildRecord(1, now, now, openCooldown: threshold <= 1, duration);
                }

                var newCount = existing.FailureCount + 1;
                var openCooldown = newCount >= threshold;
                return BuildRecord(newCount, existing.FirstFailureAt, now, openCooldown, duration);
            });
    }

    private static FailureRecord BuildRecord(
        int count, DateTimeOffset firstAt, DateTimeOffset lastAt, bool openCooldown, TimeSpan duration)
    {
        return new FailureRecord
        {
            FailureCount = count,
            FirstFailureAt = firstAt,
            LastFailureAt = lastAt,
            CooldownUntil = openCooldown ? lastAt + duration : null
        };
    }

    /// <summary>
    /// Record a success for the account — clears the failure record
    /// (cooldown if active, or just the counter). The next call sees
    /// a clean slate.
    /// </summary>
    public void RecordSuccess(string accountKey)
    {
        if (string.IsNullOrEmpty(accountKey)) return;
        _failures.TryRemove(accountKey, out _);
    }

    /// <summary>
    /// Test-only: clear all cooldown state.
    /// </summary>
    public void Reset()
    {
        _failures.Clear();
    }

    /// <summary>
    /// Current number of distinct accounts being tracked (whether or not
    /// they are on cooldown). Useful for observability and tests.
    /// </summary>
    public int TrackedAccountCount => _failures.Count;

    private sealed class FailureRecord
    {
        public int FailureCount { get; set; }
        public DateTimeOffset FirstFailureAt { get; set; }
        public DateTimeOffset LastFailureAt { get; set; }
        public DateTimeOffset? CooldownUntil { get; set; }
    }
}

/// <summary>
/// Tunable options for the cooldown tracker.
/// </summary>
public sealed class CooldownOptions
{
    /// <summary>Default: 5 failures within 60s opens a 30s cooldown.</summary>
    public static CooldownOptions Default { get; } = new()
    {
        Threshold = 5,
        Window = TimeSpan.FromSeconds(60),
        Duration = TimeSpan.FromSeconds(30)
    };

    /// <summary>Test preset: 2 failures within 5s opens a 1s cooldown.</summary>
    public static CooldownOptions Test { get; } = new()
    {
        Threshold = 2,
        Window = TimeSpan.FromSeconds(5),
        Duration = TimeSpan.FromSeconds(1)
    };

    public int Threshold { get; init; }
    public TimeSpan Window { get; init; }
    public TimeSpan Duration { get; init; }
}
