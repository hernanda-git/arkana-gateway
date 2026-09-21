namespace Arkana.Domain.Entities;

/// <summary>
/// A customer organization that uses the gateway (ENT-ARKANA-001).
/// Every data row (API keys, models, usage, logs) is scoped to a tenant.
/// The gateway can serve multiple tenants from a single database instance.
/// </summary>
public sealed class Tenant
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public string? Settings { get; private set; }

    // Navigation
    public ICollection<ApiKey> ApiKeys { get; private set; } = [];
    public ICollection<ProviderAccount> ProviderAccounts { get; private set; } = [];
    public ICollection<DashboardUser> DashboardUsers { get; private set; } = [];
    public ICollection<Webhook> Webhooks { get; private set; } = [];
    public TenantBudget? Budget { get; private set; }
    public TenantPlan? Plan { get; private set; }

    private Tenant() { } // EF Core

    public static Tenant Create(string name, string slug, string? settings = null)
    {
        return new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug.ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
            Settings = settings,
        };
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;
}
