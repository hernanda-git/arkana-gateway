using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// Queue-backed implementation of <see cref="ITokenTracker"/>
/// (PERF-ARKANA-005). <see cref="RecordUsageAsync"/> enqueues the
/// event into the <see cref="IMeteringQueue"/> and returns
/// immediately; a <c>BackgroundService</c> bulk-inserts.
///
/// Read methods (<see cref="GetUsageAsync"/>,
/// <see cref="GetTotalCostAsync"/>, <see cref="GetRecentUsageAsync"/>)
/// are not on the hot path so they keep using a scoped DbContext.
/// </summary>
public sealed class BatchedTokenTracker : ITokenTracker
{
    private readonly IMeteringQueue _queue;
    private readonly IDbContextFactory<GatewayDbContext> _dbFactory;
    private readonly ITenantProvider? _tenant;

    public BatchedTokenTracker(
        IMeteringQueue queue,
        IDbContextFactory<GatewayDbContext> dbFactory,
        ITenantProvider? tenant = null)
    {
        _queue = queue;
        _dbFactory = dbFactory;
        _tenant = tenant;
    }

    public async Task RecordUsageAsync(TokenUsage usage, CancellationToken ct = default)
    {
        if (_tenant is not null)
        {
            if (_tenant.TenantId is not { } tenantId)
                throw new InvalidOperationException("Authenticated tenant is required for token usage.");
            usage = usage with { TenantId = tenantId };
        }

        // Adapter: the ITokenTracker contract returns Task, but the
        // queue returns ValueTask<bool> so the chat handler can
        // inspect the drop flag. The bool is discarded here — the
        // metering metrics will surface drops via the OTel exporter.
        await _queue.EnqueueUsageAsync(usage, ct);
    }

    public async Task<IReadOnlyList<TokenUsage>> GetUsageAsync(
        DateTimeOffset from, DateTimeOffset until, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var fromUtc = from.ToUniversalTime();
        var toUtc = until.ToUniversalTime();
        var query = db.TokenUsages
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
        DateTimeOffset from, DateTimeOffset until, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.TokenUsages
            .Where(u => u.Timestamp >= from && u.Timestamp <= until);
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
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.TokenUsages.AsQueryable();
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

    private static TokenUsage MapToUsage(Persistence.Entities.TokenUsageEntity e) =>
        new(e.Provider, e.Model, e.InputTokens, e.OutputTokens, e.Cost,
            new TimeSpan(e.DurationTicks), e.ApiKeyName)
        {
            Timestamp = e.Timestamp,
            TenantId = e.TenantId
        };
}
