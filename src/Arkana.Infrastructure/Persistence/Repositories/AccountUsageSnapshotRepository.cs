using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Persistence.Repositories;

/// <summary>EF Core store for latest ChatGPT Codex usage snapshots.</summary>
public sealed class AccountUsageSnapshotRepository : IAccountUsageSnapshotRepository
{
    private readonly GatewayDbContext _db;
    private readonly ITenantProvider? _tenant;

    public AccountUsageSnapshotRepository(GatewayDbContext db, ITenantProvider? tenant = null)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<List<AccountUsageSnapshot>> GetByAccountCodesAsync(
        Guid tenantId, IEnumerable<string>? accountCodes = null, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            return [];

        var query = _db.AccountUsageSnapshots
            .Where(s => s.TenantId == tenantId);
        if (accountCodes is not null)
        {
            var codes = accountCodes.Where(c => !string.IsNullOrWhiteSpace(c))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (codes.Count == 0)
                return [];
            query = query.Where(s => codes.Contains(s.AccountCode));
        }
        return await query.ToListAsync(ct);
    }

    public async Task<bool> UpsertAsync(AccountUsageSnapshot snapshot, CancellationToken ct = default)
    {
        // The snapshot carries an account provider id, so resolve its tenant
        // from the authoritative provider row. This remains safe for detached
        // broker capture tasks whose fresh scope has no HttpContext tenant.
        var providerTenantId = await _db.AiProviders
            .Where(p => p.Id == snapshot.AccountProviderId)
            .Select(p => (Guid?)p.TenantId)
            .SingleOrDefaultAsync(ct);
        if (providerTenantId is not { } tenantId || tenantId == Guid.Empty)
            return false;
        if (_tenant?.TenantId is { } currentTenant && currentTenant != tenantId)
            return false;

        snapshot.AssignTenant(tenantId);
        var existing = await _db.AccountUsageSnapshots.FirstOrDefaultAsync(
            s => s.TenantId == tenantId
                && s.AccountProviderId == snapshot.AccountProviderId
                && s.WindowKind == snapshot.WindowKind,
            ct);

        if (existing is null)
        {
            await _db.AccountUsageSnapshots.AddAsync(snapshot, ct);
        }
        else
        {
            existing.UpdateFrom(snapshot.UsedPercent, snapshot.WindowMinutes, snapshot.ResetsAtUtc, snapshot.PlanType);
        }
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
