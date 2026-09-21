using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;

namespace Arkana.Domain.Services;

/// <summary>
/// SLA monitoring service — tracks provider/model performance metrics,
/// detects degradation, and exposes health status.
/// Phase 6 — Performance benchmarks and SLA monitoring.
/// Lives in the Domain layer so infrastructure (the metering writer)
/// can record metrics without referencing the API project.
/// </summary>
public sealed class SlaMonitor : ISlaMetricsRecorder
{
    private readonly ISlaRepository _repository;

    public SlaMonitor(ISlaRepository repository)
    {
        _repository = repository;
    }

    public async Task RecordSuccessAsync(string providerCode, string modelCode, double latencyMs, Guid tenantId, CancellationToken ct = default)
    {
        var metric = await GetOrCreateAsync(providerCode, modelCode, tenantId, ct);
        metric.RecordSuccess(latencyMs);
        await _repository.UpdateAsync(metric, ct);
    }

    public async Task RecordFailureAsync(string providerCode, string modelCode, double latencyMs, Guid tenantId, CancellationToken ct = default)
    {
        var metric = await GetOrCreateAsync(providerCode, modelCode, tenantId, ct);
        metric.RecordFailure(latencyMs);
        await _repository.UpdateAsync(metric, ct);
    }

    public async Task<IReadOnlyList<SlaMetric>> GetMetricsAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _repository.GetAllAsync(tenantId, ct);
    }

    public async Task<IReadOnlyList<SlaMetric>> GetUnhealthyAsync(CancellationToken ct = default)
    {
        return await _repository.GetUnhealthyAsync(ct);
    }

    public async Task<SlaMetric?> GetMetricAsync(string providerCode, string modelCode, Guid tenantId, CancellationToken ct = default)
    {
        return await _repository.GetByProviderModelAsync(providerCode, modelCode, tenantId, ct);
    }

    private async Task<SlaMetric> GetOrCreateAsync(string providerCode, string modelCode, Guid tenantId, CancellationToken ct)
    {
        var existing = await _repository.GetByProviderModelAsync(providerCode, modelCode, tenantId, ct);
        if (existing != null) return existing;

        var metric = SlaMetric.Create(providerCode, modelCode, tenantId);
        await _repository.CreateAsync(metric, ct);
        return metric;
    }
}
