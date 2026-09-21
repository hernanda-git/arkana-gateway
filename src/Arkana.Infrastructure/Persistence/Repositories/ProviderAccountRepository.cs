using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Arkana.Infrastructure.Persistence.Repositories;

internal sealed class ProviderAccountRepository : IProviderAccountRepository
{
    private readonly GatewayDbContext _db;
    public ProviderAccountRepository(GatewayDbContext db) => _db = db;

    public Task<ProviderAccount?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default)
        => Query(false).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);

    public Task<ProviderAccount?> GetByIdNoTrackingAsync(Guid tenantId, Guid id, CancellationToken ct = default)
        => Query(false).AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);

    public async Task<ProviderAccount?> GetByCodeAsync(Guid tenantId, string code, CancellationToken ct = default)
    {
        var normalized = code.Trim().ToLowerInvariant();
        var candidates = await Query(false).Where(x => x.TenantId == tenantId).ToListAsync(ct);
        return candidates.FirstOrDefault(x => string.Equals(x.Code, normalized, StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<ProviderAccount>> GetForProviderAsync(Guid tenantId, Guid providerId, bool includeDeleted = false, CancellationToken ct = default)
        => await Query(includeDeleted).Where(x => x.TenantId == tenantId && x.AiProviderId == providerId).OrderBy(x => x.Code).ToListAsync(ct);

    public async Task<IReadOnlyList<ProviderAccount>> GetHealthyAsync(Guid tenantId, Guid providerId, DateTimeOffset? at = null, CancellationToken ct = default)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        return await Query(false).Where(x => x.TenantId == tenantId && x.AiProviderId == providerId && x.IsEnabled &&
            x.ConnectionStatus == ProviderAccountStatus.Connected && (!x.CooldownUntil.HasValue || x.CooldownUntil <= now))
            .OrderBy(x => x.Code).ToListAsync(ct);
    }

    public async Task AddAsync(ProviderAccount account, CancellationToken ct = default)
    {
        await _db.ProviderAccounts.AddAsync(account, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(ProviderAccount account, CancellationToken ct = default)
    {
        _db.ProviderAccounts.Update(account);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IAsyncDisposable> AcquireMutationLeaseAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        var connectionString = _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("A database connection is required for provider-account mutation fencing.");
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("select pg_advisory_lock(hashtext(@tenant), hashtext(@account))", connection);
            command.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            command.Parameters.AddWithValue("account", id.ToString("D"));
            await command.ExecuteScalarAsync(ct);
            return new AdvisoryMutationLease(connection, tenantId, id);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<bool> TryReserveVersionAsync(Guid tenantId, Guid id, Guid expectedVersion, Guid reservationVersion, CancellationToken ct = default)
    {
        var updated = await _db.ProviderAccounts
            .Where(x => x.TenantId == tenantId && x.Id == id && x.DeletedAt == null && x.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, reservationVersion), ct);
        if (updated != 1)
            return false;
        // ExecuteUpdateAsync bypasses the change tracker: detach any tracked
        // instance so follow-up reads observe the reserved version instead of
        // the stale pre-reservation value.
        var tracked = _db.ChangeTracker.Entries<ProviderAccount>()
            .FirstOrDefault(e => e.Entity.Id == id && e.Entity.TenantId == tenantId);
        if (tracked is not null)
            tracked.State = EntityState.Detached;
        return true;
    }

    public async Task<bool> TryUpdateDataPlaneHealthAsync(ProviderAccount account, Guid expectedVersion, CancellationToken ct = default)
        => await _db.ProviderAccounts
            .Where(x => x.TenantId == account.TenantId && x.Id == account.Id && x.DeletedAt == null && x.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CooldownUntil, account.CooldownUntil)
                .SetProperty(x => x.LastSuccessAt, account.LastSuccessAt)
                .SetProperty(x => x.LastFailureAt, account.LastFailureAt)
                .SetProperty(x => x.LastFailureClass, account.LastFailureClass)
                .SetProperty(x => x.UpdatedAt, account.UpdatedAt)
                .SetProperty(x => x.Version, account.Version), ct) == 1;

    private IQueryable<ProviderAccount> Query(bool includeDeleted)
        => includeDeleted ? _db.ProviderAccounts : _db.ProviderAccounts.Where(x => x.DeletedAt == null);

    private sealed class AdvisoryMutationLease(NpgsqlConnection connection, Guid tenantId, Guid accountId) : IAsyncDisposable
    {
        private int _released;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            try
            {
                await using var command = new NpgsqlCommand("select pg_advisory_unlock(hashtext(@tenant), hashtext(@account))", connection);
                command.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
                command.Parameters.AddWithValue("account", accountId.ToString("D"));
                await command.ExecuteScalarAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
