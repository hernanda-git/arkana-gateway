using Arkana.Infrastructure.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// Periodically calls <see cref="CacheMetricsExporter.Flush"/> to convert
/// the cache's monotonic counters into OTel deltas.
///
/// Frequency:
///   10s by default. OpenTelemetry's exporter (Aspire / Prometheus /
///   OTLP) does its own scrape, so we don't need a tight loop here —
///   we just need the deltas to be pushed often enough that a 15s
///   scrape sees fresh data. 10s gives one full extra scrape cycle of
///   headroom.
/// </summary>
public sealed class CacheMetricsFlushService : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    private readonly CacheMetricsExporter _exporter;
    private readonly ILogger<CacheMetricsFlushService> _logger;

    public CacheMetricsFlushService(
        CacheMetricsExporter exporter,
        ILogger<CacheMetricsFlushService> logger)
    {
        _exporter = exporter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Cache metrics flush service started (interval={Interval}s)",
            (int)FlushInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _exporter.Flush();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never let a metric flush crash the host. A single
                // bad tick is preferable to taking the gateway down.
                _logger.LogWarning(ex, "Cache metrics flush tick failed");
            }

            try
            {
                await Task.Delay(FlushInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
