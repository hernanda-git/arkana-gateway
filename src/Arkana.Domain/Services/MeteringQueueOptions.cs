namespace Arkana.Domain.Services;

/// <summary>
/// Configuration for the batched metering queue (PERF-ARKANA-005).
/// Bound from the "MeteringQueue" section of appsettings.json so
/// operators can tune batch size, flush interval, and capacity
/// without rebuilding.
/// </summary>
public sealed class MeteringQueueOptions
{
    /// <summary>
    /// appsettings section name. Always use this constant — do not
    /// inline the string at call sites.
    /// </summary>
    public const string Section = "MeteringQueue";

    /// <summary>
    /// Master enable flag. When false, the flusher service is not
    /// registered and the metering components fall back to the
    /// synchronous path (suitable for tests or debugging where
    /// observability of the DB write per request is desired).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Max events to bundle into a single SaveChanges round-trip.
    /// 100 is a safe default — PostgreSQL handles a 100-row INSERT
    /// in &lt; 5 ms on the dev box, and at 100 RPS we already get
    /// 100x fewer round-trips than the old per-request path.
    /// </summary>
    public int MaxBatchSize { get; set; } = 100;

    /// <summary>
    /// Time-based flush trigger. Even if the batch isn't full, the
    /// flusher commits whatever is in the queue every N seconds so
    /// the dashboard sees fresh data.
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Channel capacity. Acts as the backpressure boundary — when
    /// the queue is full, Enqueue blocks the calling thread.
    /// Sized for ~10 seconds of 1k RPS load.
    /// </summary>
    public int Capacity { get; set; } = 10_000;

    /// <summary>
    /// Hard upper bound on the time the chat hot path can spend
    /// waiting to enqueue. If the channel is full beyond this, we
    /// drop the event and increment a counter — better to lose
    /// telemetry than to make a chat unresponsive.
    /// </summary>
    public TimeSpan EnqueueTimeout { get; set; } = TimeSpan.FromMilliseconds(50);
}
