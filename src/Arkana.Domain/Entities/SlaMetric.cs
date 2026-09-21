namespace Arkana.Domain.Entities;

/// <summary>
/// Tracks SLA (Service Level Agreement) metrics per provider/model.
/// Records latency percentiles, error rates, uptime, and availability.
/// Phase 6 — Performance benchmarks and SLA monitoring.
/// </summary>
public sealed class SlaMetric
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string ProviderCode { get; set; } = string.Empty;
    public string ModelCode { get; set; } = string.Empty;

    // ── Latency metrics (rolling window) ──────────────────
    public double AvgLatencyMs { get; set; }
    public double P50LatencyMs { get; set; }
    public double P95LatencyMs { get; set; }
    public double P99LatencyMs { get; set; }
    public double MaxLatencyMs { get; set; }

    // ── Throughput ─────────────────────────────────────────
    public long TotalRequests { get; set; }
    public long SuccessfulRequests { get; set; }
    public long FailedRequests { get; set; }

    // ── Error tracking ────────────────────────────────────
    public double ErrorRate { get; set; } // 0.0 - 1.0
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }

    // ── Availability ──────────────────────────────────────
    public double UptimePercent { get; set; } = 100.0;
    public bool IsHealthy { get; set; } = true;
    public DateTimeOffset LastHealthCheckAt { get; set; }

    // ── Window ────────────────────────────────────────────
    public DateTimeOffset WindowStart { get; set; }
    public DateTimeOffset WindowEnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // ── Navigation ────────────────────────────────────────
    public Tenant Tenant { get; set; } = null!;

    private SlaMetric() { } // EF Core

    public static SlaMetric Create(string providerCode, string modelCode, Guid tenantId)
    {
        return new SlaMetric
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProviderCode = providerCode,
            ModelCode = modelCode,
            WindowStart = DateTimeOffset.UtcNow,
            WindowEnd = DateTimeOffset.UtcNow,
            LastHealthCheckAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UptimePercent = 100.0,
            IsHealthy = true,
        };
    }

    /// <summary>Records a successful request with its latency.</summary>
    public void RecordSuccess(double latencyMs)
    {
        TotalRequests++;
        SuccessfulRequests++;
        ConsecutiveFailures = 0;

        // Update rolling averages
        var n = TotalRequests;
        AvgLatencyMs = ((AvgLatencyMs * (n - 1)) + latencyMs) / n;

        // Update percentiles (simplified — proper implementation uses HDR histogram)
        if (latencyMs > MaxLatencyMs) MaxLatencyMs = latencyMs;
        if (latencyMs > P99LatencyMs) P99LatencyMs = latencyMs;
        if (latencyMs > P95LatencyMs && TotalRequests > 20) P95LatencyMs = latencyMs * 0.95 + P95LatencyMs * 0.05;

        ErrorRate = TotalRequests > 0 ? (double)FailedRequests / TotalRequests : 0;
        IsHealthy = ErrorRate < 0.1 && ConsecutiveFailures < 5;
        WindowEnd = DateTimeOffset.UtcNow;
    }

    /// <summary>Records a failed request.</summary>
    public void RecordFailure(double latencyMs)
    {
        TotalRequests++;
        FailedRequests++;
        ConsecutiveFailures++;
        LastFailureAt = DateTimeOffset.UtcNow;

        ErrorRate = TotalRequests > 0 ? (double)FailedRequests / TotalRequests : 0;
        IsHealthy = ErrorRate < 0.1 && ConsecutiveFailures < 5;
        WindowEnd = DateTimeOffset.UtcNow;
    }
}
