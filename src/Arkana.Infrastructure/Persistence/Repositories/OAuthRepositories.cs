using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.Persistence.Repositories;

public sealed class OAuthProviderConfigRepository : IOAuthProviderConfigRepository
{
    private readonly GatewayDbContext _ctx;
    public OAuthProviderConfigRepository(GatewayDbContext ctx) => _ctx = ctx;

    public async Task<IReadOnlyList<OAuthProviderConfig>> GetAllAsync(CancellationToken ct = default)
    {
        var list = await _ctx.OAuthProviderConfigs.AsNoTracking().ToListAsync(ct);
        return list;
    }

    public async Task<OAuthProviderConfig?> GetByCodeAsync(string providerCode, CancellationToken ct = default)
    {
        var list = await _ctx.OAuthProviderConfigs.AsNoTracking().ToListAsync(ct);
        return list.FirstOrDefault(c => string.Equals(c.ProviderCode, providerCode, StringComparison.OrdinalIgnoreCase));
    }

    public Task<OAuthProviderConfig?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _ctx.OAuthProviderConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task AddAsync(OAuthProviderConfig config, CancellationToken ct = default)
    {
        _ctx.OAuthProviderConfigs.Add(config);
        await _ctx.SaveChangesAsync(ct);
    }
}

public sealed class ProviderOAuthTokenRepository : IProviderOAuthTokenRepository
{
    private readonly GatewayDbContext _ctx;
    public ProviderOAuthTokenRepository(GatewayDbContext ctx) => _ctx = ctx;

    public Task<ProviderOAuthToken?> GetByProviderAsync(Guid aiProviderId, Guid tenantId, CancellationToken ct = default)
        => _ctx.ProviderOAuthTokens
            .Include(t => t.Provider)
            .FirstOrDefaultAsync(t => t.AiProviderId == aiProviderId && t.TenantId == tenantId, ct);

    public async Task<IReadOnlyList<ProviderOAuthToken>> GetConnectedNeedingRefreshAsync(TimeSpan slack, CancellationToken ct = default)
    {
        var rows = await _ctx.ProviderOAuthTokens
            .Where(t => t.Status == OAuthTokenStatus.Connected && t.ExpiresAt != null)
            .ToListAsync(ct);
        return rows.Where(r => r.NeedsRefresh(slack)).ToList();
    }
    public async Task AddAsync(ProviderOAuthToken token, CancellationToken ct = default)
    {
        _ctx.ProviderOAuthTokens.Add(token);
        await _ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(ProviderOAuthToken token, CancellationToken ct = default)
    {
        var local = _ctx.ChangeTracker.Entries<ProviderOAuthToken>()
            .FirstOrDefault(e => e.Entity.Id == token.Id)?.Entity;
        if (local is null)
        {
            _ctx.ProviderOAuthTokens.Attach(token);
            local = token;
        }
        else if (!ReferenceEquals(local, token))
        {
            _ctx.Entry(local).CurrentValues.SetValues(token);
        }

        _ctx.Entry(local).State = EntityState.Modified;
        await _ctx.SaveChangesAsync(ct);
    }
}

public sealed class OAuthPendingFlowRepository : IOAuthPendingFlowRepository
{
    private readonly GatewayDbContext _ctx;
    private readonly ILogger<OAuthPendingFlowRepository>? _log;
    public OAuthPendingFlowRepository(GatewayDbContext ctx, ILogger<OAuthPendingFlowRepository>? log = null)
    {
        _ctx = ctx;
        _log = log;
    }

    public Task<OAuthPendingFlow?> GetByStateAsync(string state, Guid? tenantId, CancellationToken ct = default)
            => _ctx.OAuthPendingFlows.FirstOrDefaultAsync(f => f.State == state && (tenantId == null || f.TenantId == tenantId), ct);

    public Task<OAuthPendingFlow?> GetActiveByProviderCodeAsync(string aiProviderCode, Guid tenantId, CancellationToken ct = default)
        => _ctx.OAuthPendingFlows
            .Where(f => f.TenantId == tenantId && f.AiProviderCode == aiProviderCode && f.ProviderCode == "chatgpt")
            .OrderByDescending(f => f.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task DeletePendingForAccountAsync(string aiProviderCode, Guid tenantId, CancellationToken ct = default)
    {
        var rows = await _ctx.OAuthPendingFlows
            .Where(f => f.TenantId == tenantId
                && (f.AiProviderCode == aiProviderCode || f.ProviderCode == aiProviderCode))
            .ToListAsync(ct);
        if (rows.Count == 0) return;
        _ctx.OAuthPendingFlows.RemoveRange(rows);
        await _ctx.SaveChangesAsync(ct);
    }

    public async Task AddAsync(OAuthPendingFlow flow, CancellationToken ct = default)
    {
        _ctx.OAuthPendingFlows.Add(flow);
        await _ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(OAuthPendingFlow flow, CancellationToken ct = default)
    {
        _ctx.OAuthPendingFlows.Update(flow);
        await _ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> PersistDeviceCredentialsAsync(
        OAuthPendingFlow flow,
        Guid tenantId,
        DateTimeOffset claimedAt,
        CancellationToken ct = default)
    {
        if (string.Equals(_ctx.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal))
        {
            var inMemory = await _ctx.OAuthPendingFlows
                .FirstOrDefaultAsync(f => f.Id == flow.Id && f.TenantId == tenantId && f.ClaimedAt == claimedAt, ct);
            if (inMemory is null) return false;
            _ctx.Entry(inMemory).Property(nameof(OAuthPendingFlow.SealedAuthorizationCode)).CurrentValue = flow.SealedAuthorizationCode;
            _ctx.Entry(inMemory).Property(nameof(OAuthPendingFlow.SealedCodeVerifier)).CurrentValue = flow.SealedCodeVerifier;
            _ctx.Entry(inMemory).Property(nameof(OAuthPendingFlow.ClaimedAt)).CurrentValue = null;
            await _ctx.SaveChangesAsync(ct);
            return true;
        }

        var changed = await _ctx.OAuthPendingFlows
            .Where(f => f.Id == flow.Id && f.TenantId == tenantId && f.ClaimedAt == claimedAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(f => f.SealedAuthorizationCode, flow.SealedAuthorizationCode)
                .SetProperty(f => f.SealedCodeVerifier, flow.SealedCodeVerifier)
                .SetProperty(f => f.ClaimedAt, (DateTimeOffset?)null), ct);
        if (changed == 1)
        {
            var tracked = _ctx.OAuthPendingFlows.Local.FirstOrDefault(f => f.Id == flow.Id && f.TenantId == tenantId);
            if (tracked is not null)
            {
                _ctx.Entry(tracked).Property(nameof(OAuthPendingFlow.SealedAuthorizationCode)).CurrentValue = flow.SealedAuthorizationCode;
                _ctx.Entry(tracked).Property(nameof(OAuthPendingFlow.SealedCodeVerifier)).CurrentValue = flow.SealedCodeVerifier;
                _ctx.Entry(tracked).Property(nameof(OAuthPendingFlow.ClaimedAt)).CurrentValue = null;
            }
        }
        return changed == 1;
    }

    public async Task<bool> PersistCompletionFailureAsync(
        OAuthPendingFlow flow,
        Guid tenantId,
        DateTimeOffset claimAt,
        bool deviceCompletionClaim,
        CancellationToken ct = default)
    {
        try
        {
            if (string.Equals(_ctx.Database.ProviderName,
                    "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal))
            {
                var inMemory = await _ctx.OAuthPendingFlows.FirstOrDefaultAsync(
                    f => f.Id == flow.Id
                        && f.TenantId == tenantId
                        && (deviceCompletionClaim ? f.CompletionClaimedAt == claimAt : f.ClaimedAt == claimAt), ct);
                if (inMemory is null) return false;
                _ctx.Entry(inMemory).Property(nameof(OAuthPendingFlow.CompletionFailure)).CurrentValue = flow.CompletionFailure;
                _ctx.Entry(inMemory).Property(nameof(OAuthPendingFlow.CompletionFailedAt)).CurrentValue = flow.CompletionFailedAt;
                _ctx.Entry(inMemory).Property(deviceCompletionClaim
                    ? nameof(OAuthPendingFlow.CompletionClaimedAt)
                    : nameof(OAuthPendingFlow.ClaimedAt)).CurrentValue = null;
                await _ctx.SaveChangesAsync(ct);
                return true;
            }

            var query = _ctx.OAuthPendingFlows
                .Where(f => f.Id == flow.Id
                    && f.TenantId == tenantId
                    && (deviceCompletionClaim ? f.CompletionClaimedAt == claimAt : f.ClaimedAt == claimAt));
            var changed = deviceCompletionClaim
                ? await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(f => f.CompletionFailure, flow.CompletionFailure)
                    .SetProperty(f => f.CompletionFailedAt, flow.CompletionFailedAt)
                    .SetProperty(f => f.CompletionClaimedAt, (DateTimeOffset?)null), ct)
                : await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(f => f.CompletionFailure, flow.CompletionFailure)
                    .SetProperty(f => f.CompletionFailedAt, flow.CompletionFailedAt)
                    .SetProperty(f => f.ClaimedAt, (DateTimeOffset?)null), ct);
            if (changed != 1) return false;

            var tracked = _ctx.OAuthPendingFlows.Local.FirstOrDefault(f => f.Id == flow.Id && f.TenantId == tenantId);
            if (tracked is not null)
            {
                _ctx.Entry(tracked).Property(nameof(OAuthPendingFlow.CompletionFailure)).CurrentValue = flow.CompletionFailure;
                _ctx.Entry(tracked).Property(nameof(OAuthPendingFlow.CompletionFailedAt)).CurrentValue = flow.CompletionFailedAt;
                _ctx.Entry(tracked).Property(deviceCompletionClaim
                    ? nameof(OAuthPendingFlow.CompletionClaimedAt)
                    : nameof(OAuthPendingFlow.ClaimedAt)).CurrentValue = null;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning("OAuth completion failure marker write failed with {ExceptionType}.", ex.GetType().Name);
            try
            {
                if (deviceCompletionClaim)
                    await TryReleaseCompletionClaimAsync(flow.Id, tenantId, claimAt, CancellationToken.None);
                else
                    await TryReleaseClaimAsync(flow.Id, tenantId, claimAt, CancellationToken.None);
            }
            catch (Exception releaseEx)
            {
                _log?.LogWarning("OAuth completion claim release failed with {ExceptionType}.", releaseEx.GetType().Name);
            }
            return false;
        }
    }

    public async Task<bool> TryClaimAsync(Guid id, Guid tenantId, DateTimeOffset claimedAt, CancellationToken ct = default)
    {
        if (string.Equals(_ctx.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal))
        {
            var inMemory = await _ctx.OAuthPendingFlows
                .FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId && f.ClaimedAt == null, ct);
            if (inMemory is null) return false;
            if (!inMemory.TryClaim(claimedAt)) return false;
            await _ctx.SaveChangesAsync(ct);
            return true;
        }

        var changed = await _ctx.OAuthPendingFlows
            .Where(f => f.Id == id && f.TenantId == tenantId && f.ClaimedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.ClaimedAt, claimedAt), ct);
        if (changed == 1)
        {
            var tracked = _ctx.OAuthPendingFlows.Local.FirstOrDefault(f => f.Id == id && f.TenantId == tenantId);
            if (tracked is not null)
                _ctx.Entry(tracked).Property(nameof(OAuthPendingFlow.ClaimedAt)).CurrentValue = claimedAt;
        }
        return changed == 1;
    }

    public async Task<bool> TryReleaseClaimAsync(Guid id, Guid tenantId, DateTimeOffset claimedAt, CancellationToken ct = default)
    {
        if (string.Equals(_ctx.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal))
        {
            var inMemory = await _ctx.OAuthPendingFlows
                .FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId && f.ClaimedAt == claimedAt, ct);
            if (inMemory is null) return false;
            _ctx.Entry(inMemory).Property(nameof(OAuthPendingFlow.ClaimedAt)).CurrentValue = null;
            await _ctx.SaveChangesAsync(ct);
            return true;
        }

        var changed = await _ctx.OAuthPendingFlows
            .Where(f => f.Id == id && f.TenantId == tenantId && f.ClaimedAt == claimedAt)
            .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.ClaimedAt, (DateTimeOffset?)null), ct);
        if (changed == 1)
        {
            var tracked = _ctx.OAuthPendingFlows.Local.FirstOrDefault(f => f.Id == id && f.TenantId == tenantId);
            if (tracked is not null)
                _ctx.Entry(tracked).Property(nameof(OAuthPendingFlow.ClaimedAt)).CurrentValue = null;
        }
        return changed == 1;
    }

    public async Task<bool> TryClaimCompletionAsync(Guid id, Guid tenantId, DateTimeOffset claimedAt, CancellationToken ct = default)
    {
        if (string.Equals(_ctx.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal))
        {
            var inMemory = await _ctx.OAuthPendingFlows
                .FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId && f.CompletionClaimedAt == null, ct);
            if (inMemory is null) return false;
            _ctx.Entry(inMemory).Property(nameof(OAuthPendingFlow.CompletionClaimedAt)).CurrentValue = claimedAt;
            await _ctx.SaveChangesAsync(ct);
            return true;
        }

        var changed = await _ctx.OAuthPendingFlows
            .Where(f => f.Id == id && f.TenantId == tenantId && f.CompletionClaimedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.CompletionClaimedAt, claimedAt), ct);
        if (changed == 1)
        {
            var tracked = _ctx.OAuthPendingFlows.Local.FirstOrDefault(f => f.Id == id && f.TenantId == tenantId);
            if (tracked is not null)
                _ctx.Entry(tracked).Property(nameof(OAuthPendingFlow.CompletionClaimedAt)).CurrentValue = claimedAt;
        }
        return changed == 1;
    }

    public async Task<bool> TryReleaseCompletionClaimAsync(Guid id, Guid tenantId, DateTimeOffset claimedAt, CancellationToken ct = default)
    {
        if (string.Equals(_ctx.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal))
        {
            var inMemory = await _ctx.OAuthPendingFlows
                .FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId && f.CompletionClaimedAt == claimedAt, ct);
            if (inMemory is null) return false;
            _ctx.Entry(inMemory).Property(nameof(OAuthPendingFlow.CompletionClaimedAt)).CurrentValue = null;
            await _ctx.SaveChangesAsync(ct);
            return true;
        }

        var changed = await _ctx.OAuthPendingFlows
            .Where(f => f.Id == id && f.TenantId == tenantId && f.CompletionClaimedAt == claimedAt)
            .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.CompletionClaimedAt, (DateTimeOffset?)null), ct);
        if (changed == 1)
        {
            var tracked = _ctx.OAuthPendingFlows.Local.FirstOrDefault(f => f.Id == id && f.TenantId == tenantId);
            if (tracked is not null)
                _ctx.Entry(tracked).Property(nameof(OAuthPendingFlow.CompletionClaimedAt)).CurrentValue = null;
        }
        return changed == 1;
    }

    public async Task DeleteAsync(Guid id, Guid tenantId, CancellationToken ct = default)
    {
        var row = await _ctx.OAuthPendingFlows.FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId, ct);
        if (row is not null)
        {
            _ctx.OAuthPendingFlows.Remove(row);
            await _ctx.SaveChangesAsync(ct);
        }
    }
}
