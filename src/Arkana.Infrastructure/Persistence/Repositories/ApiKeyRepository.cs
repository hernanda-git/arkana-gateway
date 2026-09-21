using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Persistence.Repositories;

internal sealed class ApiKeyRepository : IApiKeyRepository
{
    private readonly GatewayDbContext _db;

    public ApiKeyRepository(GatewayDbContext db) => _db = db;

    public Task<ApiKey?> GetByKeyHashAsync(string keyHash, CancellationToken ct)
        => _db.ApiKeys.Include(k => k.AllowedModels).FirstOrDefaultAsync(k => k.KeyHash == keyHash, ct);

    public async Task AddAsync(ApiKey apiKey, CancellationToken ct)
    {
        await _db.ApiKeys.AddAsync(apiKey, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ApiKey>> GetAllAsync(CancellationToken ct)
        => await _db.ApiKeys.Include(k => k.AllowedModels).ToListAsync(ct);

    public async Task<IReadOnlyList<ApiKey>> GetAllAsync(Guid tenantId, CancellationToken ct)
        => await _db.ApiKeys
            .Where(k => k.TenantId == tenantId)
            .Include(k => k.AllowedModels)
            .ToListAsync(ct);

    public async Task UpdateAsync(ApiKey apiKey, CancellationToken ct)
    {
        _db.ApiKeys.Update(apiKey);
        await _db.SaveChangesAsync(ct);
    }
}
