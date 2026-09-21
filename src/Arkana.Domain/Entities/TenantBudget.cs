namespace Arkana.Domain.Entities;

/// <summary>
/// Per-tenant budget tracking with optimistic concurrency (ENT-ARKANA-003).
///
/// The budget reflects the tenant's plan allowance plus any purchased
/// top-ups. The reservation pattern uses <see cref="RowVersion"/> to
/// prevent double-spending under concurrent requests:
///
///   1. RESERVE: deduct estimated tokens, fail if insufficient (row not updated)
///   2. EXECUTE: forward request to upstream
///   3. RELEASE: return unused over-reservation
///
/// Monthly reset is handled by a background job that sets remaining back
/// to the cap on the 1st of each month.
/// </summary>
public sealed class TenantBudget
{
    public Guid TenantId { get; private set; }
    public Tenant Tenant { get; private set; } = null!;

    /// <summary>Monthly input token allowance (from plan + top-ups).</summary>
    public long MonthlyInputTokenCap { get; private set; }

    /// <summary>Monthly output token allowance (from plan + top-ups).</summary>
    public long MonthlyOutputTokenCap { get; private set; }

    /// <summary>Remaining input tokens this period (reservation-accounted).</summary>
    public long RemainingInputTokens { get; private set; }

    /// <summary>Remaining output tokens this period (reservation-accounted).</summary>
    public long RemainingOutputTokens { get; private set; }

    /// <summary>When the current budget period started.</summary>
    public DateTimeOffset PeriodStart { get; private set; }

    /// <summary>When the current budget period ends.</summary>
    public DateTimeOffset PeriodEnd { get; private set; }

    /// <summary>Optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    private TenantBudget() { } // EF Core

    public static TenantBudget Create(Tenant tenant, long inputCap, long outputCap,
        DateTimeOffset? periodStart = null, DateTimeOffset? periodEnd = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new TenantBudget
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            MonthlyInputTokenCap = inputCap,
            MonthlyOutputTokenCap = outputCap,
            RemainingInputTokens = inputCap,
            RemainingOutputTokens = outputCap,
            PeriodStart = periodStart ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero),
            PeriodEnd = periodEnd ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1),
        };
    }

    /// <summary>
    /// Update caps (e.g. on plan change). Resets remaining to new caps.
    /// </summary>
    public void UpdateCaps(long inputCap, long outputCap)
    {
        MonthlyInputTokenCap = inputCap;
        MonthlyOutputTokenCap = outputCap;
        RemainingInputTokens = inputCap;
        RemainingOutputTokens = outputCap;
    }

    /// <summary>
    /// Reset remaining tokens to the monthly cap (called by monthly reset job).
    /// </summary>
    public void ResetPeriod(DateTimeOffset newPeriodStart, DateTimeOffset newPeriodEnd)
    {
        RemainingInputTokens = MonthlyInputTokenCap;
        RemainingOutputTokens = MonthlyOutputTokenCap;
        PeriodStart = newPeriodStart;
        PeriodEnd = newPeriodEnd;
    }

    /// <summary>
    /// Effective remaining ratio for dashboard display (0.0–1.0).
    /// </summary>
    public double UsageRatio =>
        MonthlyInputTokenCap > 0
            ? 1.0 - (double)RemainingInputTokens / MonthlyInputTokenCap
            : 0.0;
}
