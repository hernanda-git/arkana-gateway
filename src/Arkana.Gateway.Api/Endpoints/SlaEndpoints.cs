using Arkana.Domain.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Arkana.Gateway.Api.Endpoints;

/// <summary>
/// SLA monitoring API endpoints.
/// Phase 6 — Performance benchmarks and SLA monitoring.
/// </summary>
public static class SlaEndpoints
{
    public static void MapSlaEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/sla").WithTags("SLA");

        // GET /admin/sla — list all SLA metrics for tenant
        group.MapGet("/", async (ISlaMetricsRecorder recorder, HttpContext ctx) =>
        {
            var tenantId = ResolveTenantId(ctx);
            var metrics = await recorder.GetMetricsAsync(tenantId);
            return Results.Ok(metrics);
        })
        .WithName("GetSlaMetrics")
        .WithOpenApi();

        // GET /admin/sla/unhealthy — list unhealthy providers/models
        group.MapGet("/unhealthy", async (ISlaMetricsRecorder recorder) =>
        {
            var unhealthy = await recorder.GetUnhealthyAsync();
            return Results.Ok(unhealthy);
        })
        .WithName("GetUnhealthySla")
        .WithOpenApi();

        // GET /admin/sla/{provider}/{model} — get specific SLA metric
        group.MapGet("/{provider}/{model}", async (string provider, string model, ISlaMetricsRecorder recorder, HttpContext ctx) =>
        {
            var tenantId = ResolveTenantId(ctx);
            var metric = await recorder.GetMetricAsync(provider, model, tenantId);
            return metric is not null ? Results.Ok(metric) : Results.NotFound();
        })
        .WithName("GetSlaMetric")
        .WithOpenApi();
    }

    private static Guid ResolveTenantId(HttpContext ctx)
    {
        // Default tenant for admin endpoints
        return Guid.TryParse(ctx.Request.Query["tenantId"], out var tid) ? tid
            : Guid.Parse("00000000-0000-0000-0000-000000000001");
    }
}
