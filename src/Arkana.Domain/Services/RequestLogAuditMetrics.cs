namespace Arkana.Domain.Services;

/// <summary>
/// Process-wide counters for request-log audit rows that were rejected or could not be written.
///
/// These rows are dropped rather than persisted (attribution must never be wrong), and — since
/// 2026-09-21 — dropping one must never abort a response either. That trade-off makes a silent
/// failure mode possible: if <c>ITenantProvider</c> stops resolving on some authentication path,
/// every request still succeeds while writing no audit row at all. The counter exists so that
/// condition is observable (and alertable) instead of invisible.
/// </summary>
public static class RequestLogAuditMetrics
{
    private static long _dropped;

    /// <summary>Audit rows dropped since the last snapshot.</summary>
    public static long DroppedTotal => Interlocked.Read(ref _dropped);

    /// <summary>Records one dropped audit row.</summary>
    public static void RecordDropped() => Interlocked.Increment(ref _dropped);

    /// <summary>
    /// Reads and resets the counter. The exporter emits monotonic per-flush deltas, so a single
    /// spike in drops is the alarm signal.
    /// </summary>
    public static long SnapshotAndResetDropped() => Interlocked.Exchange(ref _dropped, 0);
}
