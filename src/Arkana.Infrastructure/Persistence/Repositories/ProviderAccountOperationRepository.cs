using Arkana.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Persistence.Repositories;

public interface IProviderAccountOperationRepository
{
    Task<ProviderAccountOperation?> GetActiveAsync(Guid tenantId, Guid accountId, ProviderAccountOperationKind kind, CancellationToken ct = default);
    Task<ProviderAccountOperation?> GetByIdempotencyAsync(Guid tenantId, Guid accountId, ProviderAccountOperationKind kind, string key, CancellationToken ct = default);
    Task ExpireStaleAsync(Guid tenantId, Guid accountId, ProviderAccountOperationKind kind, CancellationToken ct = default);
    Task AddAsync(ProviderAccountOperation operation, CancellationToken ct = default);
    Task UpdateAsync(ProviderAccountOperation operation, CancellationToken ct = default);
}

internal sealed class ProviderAccountOperationRepository : IProviderAccountOperationRepository
{
    private readonly GatewayDbContext _db;
    public ProviderAccountOperationRepository(GatewayDbContext db) => _db = db;
    public Task<ProviderAccountOperation?> GetActiveAsync(Guid tenantId, Guid accountId, ProviderAccountOperationKind kind, CancellationToken ct = default)
        => _db.ProviderAccountOperations.Where(x => x.TenantId == tenantId && x.ProviderAccountId == accountId && x.Kind == kind && (x.State == ProviderAccountOperationState.Pending || x.State == ProviderAccountOperationState.Running) && x.ExpiresAt > DateTimeOffset.UtcNow).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
    public Task<ProviderAccountOperation?> GetByIdempotencyAsync(Guid tenantId, Guid accountId, ProviderAccountOperationKind kind, string key, CancellationToken ct = default)
        => _db.ProviderAccountOperations.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.ProviderAccountId == accountId && x.Kind == kind && x.IdempotencyKey == key, ct);
    public async Task ExpireStaleAsync(Guid tenantId, Guid accountId, ProviderAccountOperationKind kind, CancellationToken ct = default)
        => await _db.ProviderAccountOperations.Where(x => x.TenantId == tenantId && x.ProviderAccountId == accountId && x.Kind == kind && (x.State == ProviderAccountOperationState.Pending || x.State == ProviderAccountOperationState.Running) && x.ExpiresAt <= DateTimeOffset.UtcNow).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, ProviderAccountOperationState.Expired).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
    public async Task AddAsync(ProviderAccountOperation operation, CancellationToken ct = default) { await _db.ProviderAccountOperations.AddAsync(operation, ct); await _db.SaveChangesAsync(ct); }
    public async Task UpdateAsync(ProviderAccountOperation operation, CancellationToken ct = default) { _db.ProviderAccountOperations.Update(operation); await _db.SaveChangesAsync(ct); }
}
