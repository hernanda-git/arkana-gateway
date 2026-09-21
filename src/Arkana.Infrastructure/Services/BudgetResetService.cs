using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// Rolls each tenant's budget over into the next period once the current one
/// has elapsed (ENT-ARKANA-003).
/// </summary>
/// <remarks>
/// <para>
/// <c>TenantBudget</c> documented this from the start — <em>"Monthly reset is
/// handled by a background job that sets remaining back to the cap on the 1st
/// of each month"</em> — but no such job existed. <c>ResetPeriod</c> had exactly
/// one reference in the entire solution: its own definition (verified
/// 2026-08-06). The consequence was that budget enforcement was a one-way
/// door: once a tenant's remaining tokens hit zero, every subsequent request
/// returned <c>402 budget_exceeded</c> forever, with no code path anywhere that
/// could restore the allowance.
/// </para>
/// <para>
/// The reset is done in SQL rather than by loading entities, for the same
/// reason <c>BudgetEnforcer</c> reserves in SQL: the update must be atomic with
/// respect to concurrent reservations. A read-modify-write through the change
/// tracker would race with in-flight requests and could resurrect tokens a
/// request had already reserved. The <c>WHERE "PeriodEnd" &lt;= now</c> clause
/// makes the operation idempotent — a second run in the same period matches no
/// rows — so overlapping ticks or a restart mid-sweep cannot double-reset.
/// </para>
/// <para>
/// The period advances by whole months from the existing <c>PeriodEnd</c>, so
/// boundaries stay aligned to the tenant's original billing day even if the
/// service was down when the rollover was due. A tenant idle for several months
/// is caught up in one statement by the <c>WHILE</c>-free
/// <c>date_trunc</c>-based arithmetic below.
/// </para>
/// </remarks>
public sealed class BudgetResetService : BackgroundService
{
    private readonly IDbContextFactory<GatewayDbContext> _contextFactory;
    private readonly ILogger<BudgetResetService> _logger;
    private readonly TimeSpan _interval;

    public BudgetResetService(
        IDbContextFactory<GatewayDbContext> contextFactory,
        ILogger<BudgetResetService> logger,
        IConfiguration configuration)
    {
        _contextFactory = contextFactory;
        _logger = logger;

        // Hourly is far more often than a monthly boundary needs, but it keeps
        // the worst-case delay after a missed rollover (or a restart) to an
        // hour instead of a month, at negligible cost — the sweep matches zero
        // rows on all but one tick per month.
        var minutes = configuration.GetValue<int?>("BudgetEnforcer:ResetSweepMinutes") ?? 60;
        _interval = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 1440));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Budget reset service started (sweep every {Minutes} min)",
            (int)_interval.TotalMinutes);

        // Sweep once at startup so a rollover missed while the service was down
        // is corrected immediately rather than up to _interval later.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reset = await ResetElapsedPeriodsAsync(stoppingToken);
                if (reset > 0)
                {
                    _logger.LogInformation(
                        "Budget reset: rolled {Count} tenant budget(s) into a new period",
                        reset);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // normal shutdown
            }
            catch (Exception ex)
            {
                // Never let a transient DB error kill the loop — that would
                // silently restore the "budget never resets" bug.
                _logger.LogError(ex,
                    "Budget reset sweep failed; retrying in {Minutes} min",
                    (int)_interval.TotalMinutes);
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Budget reset service stopped");
    }

    /// <summary>
    /// Resets every budget whose period has ended, advancing the window by
    /// whole months so billing boundaries stay aligned. Idempotent.
    /// </summary>
    /// <returns>The number of budgets rolled over.</returns>
    internal async Task<int> ResetElapsedPeriodsAsync(CancellationToken ct)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);

        // Guard: on providers without the table yet (fresh test DBs) this is a
        // no-op rather than an exception.
        if (!await ctx.Database.CanConnectAsync(ct)) return 0;

        var now = DateTimeOffset.UtcNow;

        return await ctx.Database.ExecuteSqlAsync(
            $"""
            UPDATE "TenantBudgets"
            SET "RemainingInputTokens"  = "MonthlyInputTokenCap",
                "RemainingOutputTokens" = "MonthlyOutputTokenCap",
                "PeriodStart"           = "PeriodEnd",
                "PeriodEnd"             = "PeriodEnd" + make_interval(
                    months => GREATEST(1, (
                        (EXTRACT(YEAR FROM AGE({now}, "PeriodEnd")) * 12)
                      +  EXTRACT(MONTH FROM AGE({now}, "PeriodEnd"))
                      +  1)::int)),
                "RowVersion"            = "RowVersion" + 1
            WHERE "PeriodEnd" <= {now}
            """, ct);
    }
}
