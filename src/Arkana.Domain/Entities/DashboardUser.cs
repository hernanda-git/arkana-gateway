namespace Arkana.Domain.Entities;

/// <summary>Dashboard admin user for Blazor UI authentication.</summary>
public sealed class DashboardUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "Admin";
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    public int LoginCount { get; set; }

    public ICollection<ApiKey> ApiKeys { get; private set; } = [];

    // Multi-tenant scoping (ENT-ARKANA-001)
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    private DashboardUser() { } // EF Core

    public static DashboardUser Create(string username, string passwordHash, string role = "Admin", string? email = null)
    {
        if (!UserRoles.IsValidRole(role))
            throw new ArgumentException($"Invalid role '{role}'. Valid roles: {string.Join(", ", UserRoles.AllRoles)}", nameof(role));

        return new()
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = passwordHash,
            Role = role,
            Email = email,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Records a successful login, updating LastLoginAt and incrementing LoginCount.</summary>
    public void RecordLogin()
    {
        LastLoginAt = DateTimeOffset.UtcNow;
        LoginCount++;
    }
}
