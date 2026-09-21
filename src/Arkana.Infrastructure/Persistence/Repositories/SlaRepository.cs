using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of ISlaRepository.
/// Phase 6 — Performance benchmarks and SLA monitoring.
/// </summary>
public sealed class SlaRepository : ISlaRepository
{
    private readonly GatewayDbContext _db;

    public SlaRepository(GatewayDbContext db) => _db = db;

    public async Task<SlaMetric?> GetByProviderModelAsync(string providerCode, string modelCode, Guid tenantId, CancellationToken ct = default)
    {
        return await _db.SlaMetrics
            .FirstOrDefaultAsync(s => s.ProviderCode == providerCode && s.ModelCode == modelCode && s.TenantId == tenantId, ct);
    }

    public async Task<IReadOnlyList<SlaMetric>> GetAllAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _db.SlaMetrics
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SlaMetric>> GetUnhealthyAsync(CancellationToken ct = default)
    {
        return await _db.SlaMetrics
            .Where(s => !s.IsHealthy)
            .OrderByDescending(s => s.ErrorRate)
            .ToListAsync(ct);
    }

    public async Task CreateAsync(SlaMetric metric, CancellationToken ct = default)
    {
        _db.SlaMetrics.Add(metric);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(SlaMetric metric, CancellationToken ct = default)
    {
        _db.SlaMetrics.Update(metric);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.SlaMetrics.FindAsync(new object[] { id }, ct);
        if (entity != null)
        {
            _db.SlaMetrics.Remove(entity);
            await _db.SaveChangesAsync(ct);
        }
    }
}
