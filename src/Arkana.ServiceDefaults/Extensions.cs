using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Arkana.ServiceDefaults;

/// <summary>
/// Extension methods for registering default service defaults (OpenTelemetry, health checks).
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Adds OpenTelemetry metrics, tracing, and logging, plus health check services.
    /// </summary>
    public static IServiceCollection AddServiceDefaults(this IServiceCollection services)
    {
        // OpenTelemetry
        services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // Gateway-domain metrics: response cache (PERF-ARKANA-002),
                // batched metering (PERF-ARKANA-005), and future
                // cooldown/fallback counters. The Meters are created
                // in Arkana.Infrastructure.Observability.
                .AddMeter("Arkana.Cache")
                .AddMeter("Arkana.Metering")
                .AddMeter("Arkana.Chat"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithLogging();

        // Health checks
        services.AddHealthChecks();

        return services;
    }
}
