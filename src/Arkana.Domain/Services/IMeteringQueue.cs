using Arkana.Domain.ValueObjects;

namespace Arkana.Domain.Services;

// Suppressed: IMeteringQueue is the accurate name. The CA1711
// rule (which prefers names ending in "Collection" or similar)
// is too generic for an interface that semantically IS a queue.
#pragma warning disable CA1711
/// <summary>
/// Non-blocking enqueue API for the batched metering pipeline
/// (PERF-ARKANA-005). The chat hot path calls <see cref="EnqueueUsageAsync"/>
/// and <see cref="EnqueueLogAsync"/> on the request thread; a
/// <c>BackgroundService</c> drains the queue and bulk-inserts.
///
/// Why a queue at all? On the old path, every chat completion did
/// two sequential <c>SaveChangesAsync</c> calls
/// (<c>TokenUsages</c> then <c>RequestLogs</c>) — each one a
/// round-trip to PostgreSQL. Under load this dominated p99 latency.
/// Batching collapses N round-trips into 1, and running the work
/// off the request thread means the chat response can return the
/// moment the upstream provider responds, not the moment the DB
/// acknowledges both writes.
/// </summary>
public interface IMeteringQueue
#pragma warning restore CA1711
{
    /// <summary>
    /// Number of events currently waiting to be flushed. Exposed
    /// for health checks, dashboards, and load tests.
    /// </summary>
    int Depth { get; }

    /// <summary>
    /// Enqueue a token-usage event for batched persistence. Returns
    /// <c>true</c> if accepted, <c>false</c> if the queue was full
    /// beyond <c>EnqueueTimeout</c> and the event was dropped.
    /// </summary>
    ValueTask<bool> EnqueueUsageAsync(TokenUsage usage, CancellationToken ct = default);

    /// <summary>
    /// Enqueue a full request-log event for batched persistence.
    /// Same drop-on-backpressure semantics as
    /// <see cref="EnqueueUsageAsync"/>.
    /// </summary>
    ValueTask<bool> EnqueueLogAsync(RequestLog log, CancellationToken ct = default);

    /// <summary>
    /// Drain all currently-buffered events. Used by the flusher
    /// service on its periodic tick and on host shutdown. Returns
    /// the events drained (caller is responsible for the DB write).
    /// </summary>
    IReadOnlyList<MeteringEvent> Drain(int maxBatchSize);

    /// <summary>
    /// Diagnostic snapshot for metrics. Resets the underlying
    /// counters so callers (e.g. an OTel exporter) get deltas.
    /// </summary>
    MeteringQueueCounters SnapshotAndResetCounters();
}

/// <summary>
/// Counters maintained by the metering queue. Reset by
/// <see cref="IMeteringQueue.SnapshotAndResetCounters"/>.
/// </summary>
public sealed class MeteringQueueCounters
{
    public long UsageEnqueued { get; init; }
    public long LogEnqueued { get; init; }
    public long UsageDropped { get; init; }
    public long LogDropped { get; init; }
    public long BatchesFlushed { get; init; }
    public long EventsFlushed { get; init; }
    public long FlushFailures { get; init; }
    public TimeSpan TotalFlushDuration { get; init; }
}
