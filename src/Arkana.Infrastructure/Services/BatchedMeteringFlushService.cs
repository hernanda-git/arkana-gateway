using Arkana.Domain.Services;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// Drains the <see cref="IMeteringQueue"/> on a fixed cadence
/// and bulk-inserts the events (PERF-ARKANA-005).
///
/// Flush triggers (whichever fires first):
///   - <see cref="MeteringQueueOptions.MaxBatchSize"/> events accumulated
///   - <see cref="MeteringQueueOptions.FlushInterval"/> elapsed since last flush
///
/// On host shutdown we drain the queue synchronously so we don't
/// lose buffered telemetry when the gateway is restarted.
/// </summary>
public sealed class BatchedMeteringFlushService : BackgroundService
{
    private readonly IMeteringQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MeteringQueueOptions _options;
    private readonly ILogger<BatchedMeteringFlushService> _logger;

    public BatchedMeteringFlushService(
        IMeteringQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptions<MeteringQueueOptions> options,
        ILogger<BatchedMeteringFlushService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Batched metering flush service started (maxBatch={MaxBatch}, interval={Interval}s)",
            _options.MaxBatchSize,
            (int)_options.FlushInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Periodic flush. We don't wait for the queue to
                // reach capacity — interval-based flushing keeps
                // the dashboard fresh even at low load.
                await Task.Delay(_options.FlushInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await TryFlushAsync(stoppingToken);
        }

        // Shutdown drain. Force-flush whatever's left in the
        // queue so we don't lose buffered telemetry on restart.
        // Use a short timeout so a misbehaving DB can't block
        // shutdown forever.
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await DrainAsync(shutdownCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final metering drain on shutdown failed");
        }
    }

    private async Task TryFlushAsync(CancellationToken ct)
    {
        // Drain in chunks so a backlog of 50k events doesn't
        // hold the timer thread hostage. We keep flushing until
        // the queue is empty OR we've done a few rounds.
        for (int round = 0; round < 10; round++)
        {
            var batch = _queue.Drain(_options.MaxBatchSize);
            if (batch.Count == 0) return;
            await WriteBatchAsync(batch, ct);
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        _logger.LogInformation("Draining metering queue on shutdown");
        while (!ct.IsCancellationRequested)
        {
            var batch = _queue.Drain(_options.MaxBatchSize);
            if (batch.Count == 0) return;
            await WriteBatchAsync(batch, ct);
        }
    }

    private async Task WriteBatchAsync(IReadOnlyList<MeteringEvent> batch, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Use a scope so we can resolve a fresh DbContext
            // (the flusher is a singleton; the DbContext is scoped).
            await using var scope = _scopeFactory.CreateAsyncScope();
            var writer = scope.ServiceProvider.GetRequiredService<MeteringDbWriter>();
            await writer.WriteAsync(batch, ct);
            sw.Stop();

            // The queue is the source of truth for counters so the
            // flusher doesn't have to know how to update them.
            if (_queue is BatchedMeteringQueue concrete)
            {
                concrete.RecordFlush(batch.Count, sw.Elapsed, success: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogError(ex,
                "Metering flush failed for {Count} events; events will be lost",
                batch.Count);

            if (_queue is BatchedMeteringQueue concrete)
            {
                concrete.RecordFlush(batch.Count, sw.Elapsed, success: false);
            }
        }
    }
}
