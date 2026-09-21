using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository for managing AI provider persistence using Entity Framework Core.
/// </summary>
internal sealed class AiProviderRepository : IAiProviderRepository
{
    private readonly GatewayDbContext _db;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiProviderRepository"/> class.
    /// </summary>
    /// <param name="db">The database context.</param>
    public AiProviderRepository(GatewayDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AiProvider>> GetAllAsync(CancellationToken ct)
        => await _db.AiProviders.OrderBy(p => p.Priority).ThenBy(p => p.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<AiProvider>> GetAllAsync(Guid tenantId, CancellationToken ct)
        => await _db.AiProviders
            .Where(p => p.TenantId == tenantId)
            .OrderBy(p => p.Priority)
            .ThenBy(p => p.Name)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<AiProvider?> GetByIdAsync(Guid id, CancellationToken ct)
        => await _db.AiProviders.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<AiProvider?> GetByIdAsync(Guid id, Guid tenantId, CancellationToken ct)
        => await _db.AiProviders.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);

    /// <inheritdoc />
    public async Task<AiProvider?> GetByCodeAsync(string code, CancellationToken ct)
    {
        var list = await _db.AiProviders.ToListAsync(ct);
        return list.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AiProvider?> GetByCodeAsync(string code, Guid tenantId, CancellationToken ct)
    {
        var list = await _db.AiProviders
            .Where(p => p.TenantId == tenantId)
            .ToListAsync(ct);
        return list.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlySet<string>> GetUsedCodesAsync(Guid tenantId, CancellationToken ct)
        => (await _db.AiProviders
                .Where(p => p.TenantId == tenantId)
                .Select(p => p.Code)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task AddAsync(AiProvider provider, CancellationToken ct)
    {
        _db.AiProviders.Add(provider);
        await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(AiProvider provider, CancellationToken ct)
    {
        _db.AiProviders.Update(provider);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(AiProvider provider, Guid tenantId, CancellationToken ct)
    {
        var existing = await _db.AiProviders
            .FirstOrDefaultAsync(p => p.Id == provider.Id && p.TenantId == tenantId, ct);
        if (existing is null)
            return;

        _db.Entry(existing).CurrentValues.SetValues(provider);
        await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var provider = await _db.AiProviders.FindAsync([id], ct);
        if (provider is not null)
        {
            _db.AiProviders.Remove(provider);
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, Guid tenantId, CancellationToken ct)
    {
        var provider = await _db.AiProviders
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
        if (provider is not null)
        {
            _db.AiProviders.Remove(provider);
            await _db.SaveChangesAsync(ct);
        }
    }
}
