using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of IApiKeyPoolRepository.
/// </summary>
public sealed class ApiKeyPoolRepository : IApiKeyPoolRepository
{
    private readonly GatewayDbContext _db;

    public ApiKeyPoolRepository(GatewayDbContext db) => _db = db;

    public async Task<ApiKeyPool?> GetByProviderAsync(Guid aiProviderId, Guid tenantId, CancellationToken ct = default)
    {
        return await _db.ApiKeyPools
            .Include(p => p.Entries)
            .FirstOrDefaultAsync(p => p.AiProviderId == aiProviderId && p.TenantId == tenantId, ct);
    }

    public async Task<ApiKeyPool?> GetByIdAsync(Guid poolId, CancellationToken ct = default)
    {
        return await _db.ApiKeyPools
            .Include(p => p.Entries)
            .FirstOrDefaultAsync(p => p.Id == poolId, ct);
    }

    public async Task<IReadOnlyList<ApiKeyPool>> GetAllAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _db.ApiKeyPools
            .Include(p => p.Entries)
            .Where(p => p.TenantId == tenantId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task CreateAsync(ApiKeyPool pool, CancellationToken ct = default)
    {
        _db.ApiKeyPools.Add(pool);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(ApiKeyPool pool, CancellationToken ct = default)
    {
        _db.ApiKeyPools.Update(pool);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid poolId, CancellationToken ct = default)
    {
        var entity = await _db.ApiKeyPools.FindAsync(new object[] { poolId }, ct);
        if (entity != null)
        {
            _db.ApiKeyPools.Remove(entity);
            await _db.SaveChangesAsync(ct);
        }
    }
}
