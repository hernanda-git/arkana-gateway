using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Repository for SLA metrics tracking and querying.
/// Phase 6 — Performance benchmarks and SLA monitoring.
/// </summary>
public interface ISlaRepository
{
    Task<SlaMetric?> GetByProviderModelAsync(string providerCode, string modelCode, Guid tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<SlaMetric>> GetAllAsync(Guid tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<SlaMetric>> GetUnhealthyAsync(CancellationToken ct = default);
    Task CreateAsync(SlaMetric metric, CancellationToken ct = default);
    Task UpdateAsync(SlaMetric metric, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
