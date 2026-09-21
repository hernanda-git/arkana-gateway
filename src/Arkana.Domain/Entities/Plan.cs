namespace Arkana.Domain.Entities;

/// <summary>
/// A pricing plan defining feature access and usage limits (ENT-ARKANA-002).
/// Plans are immutable reference data — seeded at migration time, updated
/// via DB migration or admin scripts.
/// </summary>
public sealed class Plan
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public decimal MonthlyPrice { get; private set; }

    // Included usage (resets monthly)
    public long IncludedInputTokens { get; private set; }
    public long IncludedOutputTokens { get; private set; }

    // Base rate limits
    public int MaxRequestsPerMinute { get; private set; } = 60;
    public int MaxTokensPerMinute { get; private set; } = 1_000_000;
    public int MaxConcurrent { get; private set; } = 5;
    public int MaxApiKeys { get; private set; } = 10;

    // Feature flags as JSON blob
    public string? Features { get; private set; }

    public bool IsActive { get; private set; } = true;

    // Navigation
    public ICollection<TenantPlan> TenantAssignments { get; private set; } = [];

    private Plan() { } // EF Core

    public static Plan Create(string name, string slug, decimal monthlyPrice,
        long includedInput, long includedOutput,
        int rpm = 60, int tpm = 1_000_000, int concurrent = 5, int maxKeys = 10,
        string? features = null)
    {
        return new Plan
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug.ToLowerInvariant(),
            MonthlyPrice = monthlyPrice,
            IncludedInputTokens = includedInput,
            IncludedOutputTokens = includedOutput,
            MaxRequestsPerMinute = rpm,
            MaxTokensPerMinute = tpm,
            MaxConcurrent = concurrent,
            MaxApiKeys = maxKeys,
            Features = features,
        };
    }
}

/// <summary>
/// Junction: which plan a tenant is on, with optional overrides (ENT-ARKANA-002).
/// A tenant can have only one active plan assignment at a time.
/// Overrides let sales negotiate custom terms without creating new plans.
/// </summary>
public sealed class TenantPlan
{
    public Guid TenantId { get; private set; }
    public Tenant Tenant { get; private set; } = null!;
    public Guid PlanId { get; private set; }
    public Plan Plan { get; private set; } = null!;

    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }

    // Per-tenant overrides (null = use plan defaults)
    public long? OverrideIncludedInputTokens { get; private set; }
    public long? OverrideIncludedOutputTokens { get; private set; }
    public int? OverrideMaxRpm { get; private set; }
    public int? OverrideMaxTpm { get; private set; }
    public int? OverrideMaxConcurrent { get; private set; }
    public int? OverrideMaxApiKeys { get; private set; }

    private TenantPlan() { } // EF Core

    public static TenantPlan Assign(Tenant tenant, Plan plan, DateTimeOffset? endsAt = null)
    {
        return new TenantPlan
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            PlanId = plan.Id,
            Plan = plan,
            StartsAt = DateTimeOffset.UtcNow,
            EndsAt = endsAt,
        };
    }

    // Effective values (override > plan default)
    public long EffectiveInputTokens => OverrideIncludedInputTokens ?? Plan.IncludedInputTokens;
    public long EffectiveOutputTokens => OverrideIncludedOutputTokens ?? Plan.IncludedOutputTokens;
    public int EffectiveRpm => OverrideMaxRpm ?? Plan.MaxRequestsPerMinute;
    public int EffectiveTpm => OverrideMaxTpm ?? Plan.MaxTokensPerMinute;
    public int EffectiveMaxConcurrent => OverrideMaxConcurrent ?? Plan.MaxConcurrent;
    public int EffectiveMaxApiKeys => OverrideMaxApiKeys ?? Plan.MaxApiKeys;
    public bool HasActivePlan => EndsAt is null || EndsAt > DateTimeOffset.UtcNow;
}
