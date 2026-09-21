using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Records SLA metrics for provider/model pairs. Implemented in
/// the Domain layer so infrastructure (e.g. the metering flush
/// writer) can record without a dependency on the API project.
/// Phase 6 — Performance benchmarks and SLA monitoring.
/// </summary>
public interface ISlaMetricsRecorder
{
    Task RecordSuccessAsync(string providerCode, string modelCode, double latencyMs, Guid tenantId, CancellationToken ct = default);
    Task RecordFailureAsync(string providerCode, string modelCode, double latencyMs, Guid tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<SlaMetric>> GetMetricsAsync(Guid tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<SlaMetric>> GetUnhealthyAsync(CancellationToken ct = default);
    Task<SlaMetric?> GetMetricAsync(string providerCode, string modelCode, Guid tenantId, CancellationToken ct = default);
}
