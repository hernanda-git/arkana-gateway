using Arkana.Domain.Entities;
using Arkana.Domain.Services;
using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// Budget enforcement with the reservation pattern (ENT-ARKANA-003).
/// Uses optimistic concurrency (PostgreSQL RowVersion) to prevent
/// double-spending under concurrent requests.
///
/// Thread safety: the EF Core DbContext is scoped; this service is a
/// singleton and creates fresh scopes for each operation to keep
/// concurrency isolation clean.
/// </summary>
internal sealed class BudgetEnforcer : IBudgetEnforcer
{
    private readonly IDbContextFactory<GatewayDbContext> _contextFactory;
    private readonly ILogger<BudgetEnforcer> _logger;

    /// <summary>
    /// Master toggle. Budget enforcement is opt-in via configuration
    /// or environment variable. Default: disabled (backward compatible).
    /// </summary>
    public bool IsEnabled { get; }

    public BudgetEnforcer(
        IDbContextFactory<GatewayDbContext> contextFactory,
        ILogger<BudgetEnforcer> logger,
        bool enabled = false)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        IsEnabled = enabled;
    }

    public async Task<bool> TryReserveAsync(Guid tenantId,
        long estimatedInput, long estimatedOutput,
        CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);

        // Optimistic concurrency: update only if sufficient budget remains.
        // RowVersion is auto-incremented by PostgreSQL on each successful update.
        var affected = await ctx.Database.ExecuteSqlAsync(
            $"""
            UPDATE "TenantBudgets"
            SET "RemainingInputTokens" = "RemainingInputTokens" - {estimatedInput},
                "RemainingOutputTokens" = "RemainingOutputTokens" - {estimatedOutput},
                "RowVersion" = "RowVersion" + 1
            WHERE "TenantId" = {tenantId}
              AND "RemainingInputTokens" >= {estimatedInput}
              AND "RemainingOutputTokens" >= {estimatedOutput}
            """, ct);

        if (affected > 0) return true;

        _logger.LogWarning(
            "Budget reservation FAILED for tenant {TenantId}: requested {Input}i/{Output}o, insufficient remaining",
            tenantId, estimatedInput, estimatedOutput);
        return false;
    }

    public async Task ReleaseAsync(Guid tenantId,
        long reservedInput, long reservedOutput,
        long actualInput, long actualOutput,
        CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);

        var unusedInput = Math.Max(0, reservedInput - actualInput);
        var unusedOutput = Math.Max(0, reservedOutput - actualOutput);

        if (unusedInput <= 0 && unusedOutput <= 0) return;

        await ctx.Database.ExecuteSqlAsync(
            $"""
            UPDATE "TenantBudgets"
            SET "RemainingInputTokens" = "RemainingInputTokens" + {unusedInput},
                "RemainingOutputTokens" = "RemainingOutputTokens" + {unusedOutput},
                "RowVersion" = "RowVersion" + 1
            WHERE "TenantId" = {tenantId}
            """, ct);

        _logger.LogDebug(
            "Budget release for tenant {TenantId}: returned {Input}i/{Output}o (reserved={ResI}i/{ResO}o, actual={ActI}i/{ActO}o)",
            tenantId, unusedInput, unusedOutput,
            reservedInput, reservedOutput, actualInput, actualOutput);
    }
}
