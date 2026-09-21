using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
// Aliases make the channel API less verbose at call sites.
using BoundedChannel = System.Threading.Channels.BoundedChannelOptions;
using BoundedFullMode = System.Threading.Channels.BoundedChannelFullMode;

namespace Arkana.Infrastructure.Services;

#pragma warning disable CA1711 // Suppressed: BatchedMeteringQueue is the accurate name (see IMeteringQueue).
/// <summary>
/// <see cref="IMeteringQueue"/> backed by a bounded
/// <see cref="System.Threading.Channels.Channel{T}"/>. Designed for
/// the chat hot path: every chat completion enqueues 1-2 events
/// (one <see cref="TokenUsage"/>, one <see cref="RequestLog"/>) and
/// returns immediately. A <c>BackgroundService</c> drains the
/// channel and bulk-inserts.
///
/// Backpressure: when the channel is full, <see cref="EnqueueUsageAsync"/>
/// and <see cref="EnqueueLogAsync"/> wait up to
/// <see cref="MeteringQueueOptions.EnqueueTimeout"/>. If the wait
/// expires, the event is dropped and a counter is incremented. This
/// matches the &quot;better to lose telemetry than to make a chat
/// unresponsive&quot; posture we adopted for cache writes (PERF-ARKANA-002).
///
/// Concurrency: <see cref="Drain"/> is single-threaded by design —
/// the flusher is the only consumer, and serializing the drain means
/// we don't need a lock around the channel's read side.
/// </summary>
public sealed class BatchedMeteringQueue : IMeteringQueue
#pragma warning restore CA1711
{
    private readonly System.Threading.Channels.Channel<MeteringEvent> _channel;
    private readonly MeteringQueueOptions _options;
    private readonly ILogger<BatchedMeteringQueue> _logger;

    // Counters — mutated under _counterLock so the snapshot/reset
    // pair is consistent. The lock is never held during channel I/O.
    private readonly object _counterLock = new();
    private long _usageEnqueued;
    private long _logEnqueued;
    private long _usageDropped;
    private long _logDropped;
    private long _batchesFlushed;
    private long _eventsFlushed;
    private long _flushFailures;
    private long _totalFlushDurationTicks;

    public BatchedMeteringQueue(
        IOptions<MeteringQueueOptions> options,
        ILogger<BatchedMeteringQueue> logger)
    {
        _options = options.Value;
        _logger = logger;

        _channel = System.Threading.Channels.Channel.CreateBounded<MeteringEvent>(
            new BoundedChannel(_options.Capacity)
            {
                // Wait is the safer default. SingleReader (the
                // flusher) means we don't need to coordinate
                // concurrent drains.
                FullMode = BoundedFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    /// <inheritdoc/>
    public int Depth => _channel.Reader.Count;

    /// <inheritdoc/>
    public async ValueTask<bool> EnqueueUsageAsync(TokenUsage usage, CancellationToken ct = default)
    {
        return await EnqueueAsync(new MeteringEvent.Usage(usage), isUsage: true, ct);
    }

    /// <inheritdoc/>
    public async ValueTask<bool> EnqueueLogAsync(RequestLog log, CancellationToken ct = default)
    {
        return await EnqueueAsync(new MeteringEvent.LogEntry(log), isUsage: false, ct);
    }

    /// <inheritdoc/>
    public IReadOnlyList<MeteringEvent> Drain(int maxBatchSize)
    {
        var batch = new List<MeteringEvent>(Math.Min(maxBatchSize, 64));
        while (batch.Count < maxBatchSize && _channel.Reader.TryRead(out var evt))
        {
            batch.Add(evt);
        }
        return batch;
    }

    /// <inheritdoc/>
    public MeteringQueueCounters SnapshotAndResetCounters()
    {
        // 64-bit reads of `long` fields are atomic on x64, so the
        // *individual* reads are torn-free. The group read+reset
        // is serialized via a dedicated lock so the snapshot is
        // internally consistent — otherwise a flusher running at
        // the same time could double-count a batch in the next
        // snapshot's "flushed" tally.
        lock (_counterLock)
        {
            var snapshot = new MeteringQueueCounters
            {
                UsageEnqueued = _usageEnqueued,
                LogEnqueued = _logEnqueued,
                UsageDropped = _usageDropped,
                LogDropped = _logDropped,
                BatchesFlushed = _batchesFlushed,
                EventsFlushed = _eventsFlushed,
                FlushFailures = _flushFailures,
                TotalFlushDuration = TimeSpan.FromTicks(_totalFlushDurationTicks),
            };
            _usageEnqueued = 0;
            _logEnqueued = 0;
            _usageDropped = 0;
            _logDropped = 0;
            _batchesFlushed = 0;
            _eventsFlushed = 0;
            _flushFailures = 0;
            _totalFlushDurationTicks = 0;
            return snapshot;
        }
    }

    /// <summary>
    /// Internal helper used by the flusher to record batch outcomes
    /// without exposing the mutating surface. Counters are exposed
    /// for tests; the flusher uses this path so we don't take a
    /// lock from the timer thread.
    /// </summary>
    internal void RecordFlush(int eventCount, TimeSpan duration, bool success)
    {
        if (eventCount <= 0) return;
        lock (_counterLock)
        {
            _batchesFlushed++;
            _eventsFlushed += eventCount;
            _totalFlushDurationTicks += duration.Ticks;
            if (!success) _flushFailures++;
        }
    }

    private async ValueTask<bool> EnqueueAsync(MeteringEvent evt, bool isUsage, CancellationToken ct)
    {
        // Apply our own timeout. Channel.Writer.WaitToWriteAsync
        // would also work, but the caller passes a CancellationToken
        // that may not represent our backpressure deadline — using
        // a linked CTS keeps the two concerns separate.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.EnqueueTimeout);

        try
        {
            if (await _channel.Writer.WaitToWriteAsync(cts.Token))
            {
                if (_channel.Writer.TryWrite(evt))
                {
                    lock (_counterLock)
                    {
                        if (isUsage) _usageEnqueued++;
                        else _logEnqueued++;
                    }
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our internal timeout fired, not the caller's CT.
            // Fall through to the drop path.
        }

        lock (_counterLock)
        {
            if (isUsage) _usageDropped++;
            else _logDropped++;
        }

        _logger.LogWarning(
            "Metering queue full; dropped {Kind} event (depth={Depth}/{Capacity})",
            isUsage ? "usage" : "log",
            Depth,
            _options.Capacity);
        return false;
    }
}
