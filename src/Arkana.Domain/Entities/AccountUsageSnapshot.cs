using System.Text.Json;

namespace Arkana.Domain.Entities;

/// <summary>Which rolling quota window a snapshot describes.</summary>
public enum UsageWindowKind
{
    /// <summary>The ~5-hour primary window.</summary>
    Primary = 0,

    /// <summary>The ~weekly secondary window.</summary>
    Secondary = 1,
}

/// <summary>
/// Latest known ChatGPT Codex subscription usage for one account × window.
///
/// Source of truth is the upstream's own rate-limit reporting (the
/// <c>x-codex-primary-used-percent</c> / <c>x-codex-secondary-used-percent</c>
/// header family and the equivalent <c>codex.rate_limits</code> SSE event —
/// both parsed by the official Codex client). The gateway persists the most
/// recent observation per (account, window) every time an upstream response
/// arrives, so the profile page can render usage windows that are otherwise
/// invisible. Last snapshot wins; there is no history.
/// </summary>
public sealed class AccountUsageSnapshot
{
    public Guid Id { get; private set; }

    /// <summary>AiProviders.Id of the ChatGPT account (chatgpt-accN).</summary>
    public Guid AccountProviderId { get; private set; }

    /// <summary>Denormalized provider code (chatgpt-acc1..4) for display/join-free reads.</summary>
    public string AccountCode { get; private set; } = string.Empty;

    /// <summary>Which quota window this row describes.</summary>
    public UsageWindowKind WindowKind { get; private set; }

    /// <summary>Upstream-reported percent of the window consumed (0–100+).</summary>
    public double UsedPercent { get; private set; }

    /// <summary>Upstream-reported window length in minutes, when reported.</summary>
    public int? WindowMinutes { get; private set; }

    /// <summary>UTC instant when the upstream says the window resets (epoch-seconds converted).</summary>
    public DateTimeOffset? ResetsAtUtc { get; private set; }

    /// <summary>When the gateway last observed/refreshed this snapshot.</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Plan type reported alongside the rate limits (e.g. "plus", "team").</summary>
    public string? PlanType { get; private set; }

    /// <summary>Tenant owning the provider account that produced this snapshot.</summary>
    public Guid TenantId { get; private set; }

    private AccountUsageSnapshot() { } // EF Core

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1805: Do not initialize unnecessarily",
        Justification = "Guid.NewGuid() keeps EF happy without a value generator")]
    public AccountUsageSnapshot(
        Guid accountProviderId, string accountCode, UsageWindowKind windowKind,
        double usedPercent, int? windowMinutes, DateTimeOffset? resetsAtUtc,
        string? planType, Guid tenantId = default)
    {
        if (string.IsNullOrWhiteSpace(accountCode))
            throw new ArgumentException("Account code is required.", nameof(accountCode));

        Id = Guid.NewGuid();
        AccountProviderId = accountProviderId;
        AccountCode = accountCode.Trim();
        WindowKind = windowKind;
        UsedPercent = usedPercent;
        WindowMinutes = windowMinutes;
        ResetsAtUtc = resetsAtUtc;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        PlanType = string.IsNullOrWhiteSpace(planType) ? null : planType!.Trim();
        TenantId = tenantId;
    }

    /// <summary>Assigns the owning tenant once, rejecting cross-tenant reuse.</summary>
    public void AssignTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (TenantId != Guid.Empty && TenantId != tenantId)
            throw new InvalidOperationException("Snapshot tenant cannot be changed.");
        TenantId = tenantId;
    }

    /// <summary>Applies a newer upstream observation onto this row (last-writer-wins upsert).</summary>
    public void UpdateFrom(double usedPercent, int? windowMinutes, DateTimeOffset? resetsAtUtc, string? planType)
    {
        UsedPercent = usedPercent;
        WindowMinutes = windowMinutes;
        ResetsAtUtc = resetsAtUtc;
        PlanType = string.IsNullOrWhiteSpace(planType) ? null : planType!.Trim();
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
