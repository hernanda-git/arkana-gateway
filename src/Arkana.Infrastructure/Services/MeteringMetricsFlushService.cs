using Arkana.Infrastructure.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// Background flush loop for <see cref="MeteringMetricsExporter"/>.
/// Same 10-second cadence as the cache metrics flusher so the two
/// are always in lock-step on dashboards (PERF-ARKANA-005).
/// </summary>
public sealed class MeteringMetricsFlushService : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    private readonly MeteringMetricsExporter _exporter;
    private readonly ILogger<MeteringMetricsFlushService> _logger;

    public MeteringMetricsFlushService(
        MeteringMetricsExporter exporter,
        ILogger<MeteringMetricsFlushService> logger)
    {
        _exporter = exporter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Metering metrics flush service started (interval={Interval}s)",
            (int)FlushInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _exporter.Flush();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Metering metrics flush tick failed");
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
