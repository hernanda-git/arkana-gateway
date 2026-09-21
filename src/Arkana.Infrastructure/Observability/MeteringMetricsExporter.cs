using System.Diagnostics.Metrics;
using Arkana.Domain.Services;

namespace Arkana.Infrastructure.Observability;

/// <summary>
/// Bridges <see cref="BatchedMeteringQueue"/> counters to OpenTelemetry
/// via the <c>System.Diagnostics.Metrics</c> API
/// (PERF-ARKANA-005).
///
/// One Meter hosts all metering-related instruments. The OTel
/// pipeline is already listening on this source (see
/// <c>ServiceDefaults</c> registration of <c>AddMeter(...)</c>).
///
/// Counter semantics: <c>arkana.metering.usage_enqueued</c> and
/// <c>arkana.metering.log_enqueued</c> are monotonic per-flush
/// deltas (like the cache exporter). The <c>dropped</c> counters
/// are emitted as deltas too — a single spike in drops is a useful
/// alarm signal.
/// </summary>
public sealed class MeteringMetricsExporter : IDisposable
{
    /// <summary>
    /// Name of the <see cref="Meter"/> hosting metering-pipeline metrics.
    /// </summary>
    public const string MeterName = "Arkana.Metering";

    private readonly IMeteringQueue _queue;
    private readonly Meter _meter;
    private readonly Counter<long> _usageEnqueued;
    private readonly Counter<long> _logEnqueued;
    private readonly Counter<long> _usageDropped;
    private readonly Counter<long> _logDropped;
    private readonly Counter<long> _auditDropped;
    private readonly Counter<long> _batchesFlushed;
    private readonly Counter<long> _eventsFlushed;
    private readonly Counter<long> _flushFailures;
    private readonly ObservableGauge<int> _queueDepth;

    public MeteringMetricsExporter(IMeteringQueue queue)
    {
        _queue = queue;
        _meter = new Meter(MeterName, "1.0.0");

        _usageEnqueued = _meter.CreateCounter<long>(
            "arkana.metering.usage_enqueued",
            "events",
            "Token-usage events successfully enqueued for batched persistence.");

        _logEnqueued = _meter.CreateCounter<long>(
            "arkana.metering.log_enqueued",
            "events",
            "Request-log events successfully enqueued for batched persistence.");

        _usageDropped = _meter.CreateCounter<long>(
            "arkana.metering.usage_dropped",
            "events",
            "Token-usage events dropped because the queue was full beyond EnqueueTimeout.");

        _logDropped = _meter.CreateCounter<long>(
            "arkana.metering.log_dropped",
            "events",
            "Request-log events dropped because the queue was full beyond EnqueueTimeout.");

        _auditDropped = _meter.CreateCounter<long>(
            "arkana.metering.audit_dropped",
            "events",
            "Request-log audit rows dropped before reaching the queue (invalid attribution or a failed write). "
            + "Token/cost usage rows still land; only the log-detail tier is lost.");

        _batchesFlushed = _meter.CreateCounter<long>(
            "arkana.metering.batches_flushed",
            "batches",
            "Total number of DB-write batches attempted by the flusher.");

        _eventsFlushed = _meter.CreateCounter<long>(
            "arkana.metering.events_flushed",
            "events",
            "Total number of metering events successfully persisted to PostgreSQL.");

        _flushFailures = _meter.CreateCounter<long>(
            "arkana.metering.flush_failures",
            "failures",
            "Batches that failed to persist. Events in failed batches are LOST (logged at error).");

        // Live queue depth — O(1) read. Useful for backpressure
        // alarms and load tests.
        _queueDepth = _meter.CreateObservableGauge(
            "arkana.metering.queue_depth",
            () => _queue.Depth,
            "events",
            "Number of metering events currently waiting to be flushed.");
    }

    /// <summary>
    /// Snapshot the queue's monotonic counters, emit deltas, reset.
    /// Called periodically by <c>MeteringMetricsFlushService</c>
    /// (same cadence as the cache flusher — 10s).
    /// </summary>
    public void Flush()
    {
        var c = _queue.SnapshotAndResetCounters();
        if (c.UsageEnqueued > 0) _usageEnqueued.Add(c.UsageEnqueued);
        if (c.LogEnqueued > 0) _logEnqueued.Add(c.LogEnqueued);
        if (c.UsageDropped > 0) _usageDropped.Add(c.UsageDropped);
        if (c.LogDropped > 0) _logDropped.Add(c.LogDropped);
        var auditDropped = RequestLogAuditMetrics.SnapshotAndResetDropped();
        if (auditDropped > 0) _auditDropped.Add(auditDropped);
        if (c.BatchesFlushed > 0) _batchesFlushed.Add(c.BatchesFlushed);
        if (c.EventsFlushed > 0) _eventsFlushed.Add(c.EventsFlushed);
        if (c.FlushFailures > 0) _flushFailures.Add(c.FlushFailures);
    }

    public void Dispose() => _meter.Dispose();
}
