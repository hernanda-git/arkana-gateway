using Arkana.Domain.Interfaces;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// PostgreSQL-backed token tracker. Replaces InMemoryTokenTracker.
/// </summary>
internal sealed class EfCoreTokenTracker : ITokenTracker
{
    private readonly GatewayDbContext _db;
    private readonly ITenantProvider? _tenant;

    public EfCoreTokenTracker(GatewayDbContext db, ITenantProvider? tenant = null)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task RecordUsageAsync(TokenUsage usage, CancellationToken ct = default)
    {
        var tenantId = _tenant?.TenantId ?? (usage.TenantId == Guid.Empty ? null : usage.TenantId);
        if (_tenant is not null && tenantId is null)
            throw new InvalidOperationException("Authenticated tenant is required for token usage.");

        var entity = new TokenUsageEntity
        {
            Id = Guid.NewGuid(),
            Provider = usage.Provider,
            Model = usage.Model,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            Cost = usage.Cost,
            DurationTicks = usage.Duration.Ticks,
            Timestamp = usage.Timestamp.ToUniversalTime(),
            ApiKeyName = usage.ApiKeyName,
            TenantId = tenantId ?? usage.TenantId
        };

        _db.TokenUsages.Add(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TokenUsage>> GetUsageAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();
        var query = _db.TokenUsages
            .Where(u => u.Timestamp >= fromUtc && u.Timestamp <= toUtc);
        if (_tenant is not null)
        {
            if (_tenant.TenantId is not { } tenantId)
                return [];
            query = query.Where(u => u.TenantId == tenantId);
        }

        var entities = await query
            .OrderByDescending(u => u.Timestamp)
            .ToListAsync(ct);

        return entities.Select(MapToUsage).ToList();
    }

    public async Task<decimal> GetTotalCostAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var query = _db.TokenUsages
            .Where(u => u.Timestamp >= from && u.Timestamp <= to);
        if (_tenant is not null)
        {
            if (_tenant.TenantId is not { } tenantId)
                return 0m;
            query = query.Where(u => u.TenantId == tenantId);
        }

        return await query.SumAsync(u => u.Cost, ct);
    }

    public async Task<IReadOnlyList<TokenUsage>> GetRecentUsageAsync(
        int count = 50, CancellationToken ct = default)
    {
        var query = _db.TokenUsages.AsQueryable();
        if (_tenant is not null)
        {
            if (_tenant.TenantId is not { } tenantId)
                return [];
            query = query.Where(u => u.TenantId == tenantId);
        }

        var entities = await query
            .OrderByDescending(u => u.Timestamp)
            .Take(count)
            .ToListAsync(ct);

        return entities.Select(MapToUsage).ToList();
    }

    private static TokenUsage MapToUsage(TokenUsageEntity e) =>
        new(e.Provider, e.Model, e.InputTokens, e.OutputTokens, e.Cost,
            new TimeSpan(e.DurationTicks), e.ApiKeyName)
        {
            Timestamp = e.Timestamp,
            TenantId = e.TenantId
        };
}
