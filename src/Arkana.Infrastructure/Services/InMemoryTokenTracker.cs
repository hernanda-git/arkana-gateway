using Arkana.Domain.Interfaces;
using Arkana.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// In-memory token tracker (will be replaced by PostgreSQL + Redis persistence).
/// </summary>
internal sealed class InMemoryTokenTracker : ITokenTracker
{
    private readonly ConcurrentBag<TokenUsage> _usages = [];
    private readonly ITenantProvider? _tenant;

    public InMemoryTokenTracker(ITenantProvider? tenant = null)
        => _tenant = tenant;

    public Task RecordUsageAsync(TokenUsage usage, CancellationToken ct)
    {
        if (_tenant is not null)
        {
            if (_tenant.TenantId is not { } tenantId)
                return Task.FromException(new InvalidOperationException("Authenticated tenant is required for token usage."));
            usage = usage with { TenantId = tenantId };
        }

        _usages.Add(usage);
        return Task.CompletedTask;
    }

    private IEnumerable<TokenUsage> TenantScoped()
    {
        if (_tenant is null)
            return _usages;
        return _tenant.TenantId is { } tenantId
            ? _usages.Where(u => u.TenantId == tenantId)
            : [];
    }

    public Task<IReadOnlyList<TokenUsage>> GetUsageAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var result = TenantScoped()
            .Where(u => u.Timestamp >= from && u.Timestamp <= to)
            .ToList() as IReadOnlyList<TokenUsage>;

        return Task.FromResult(result);
    }

    public Task<decimal> GetTotalCostAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var total = TenantScoped()
            .Where(u => u.Timestamp >= from && u.Timestamp <= to)
            .Sum(u => u.Cost);
        return Task.FromResult(total);
    }

    public Task<IReadOnlyList<TokenUsage>> GetRecentUsageAsync(int count = 50, CancellationToken ct = default)
    {
        var result = TenantScoped()
            .OrderByDescending(u => u.Timestamp)
            .Take(count)
            .ToList() as IReadOnlyList<TokenUsage>;
        return Task.FromResult(result);
    }
}
