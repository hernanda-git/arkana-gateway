namespace Arkana.Domain.Entities;

/// <summary>
/// An AI model belonging to a provider (e.g., "deepseek-v4-flash" under OpenCode).
/// </summary>
public sealed class Model
{
    public Guid Id { get; private set; }
    public Guid ProviderId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public bool IsEnabled { get; private set; } = true;
    public decimal CostPerInputToken { get; private set; }
    public decimal CostPerOutputToken { get; private set; }
    public int? MaxTokensPerRequest { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    // Navigation
    public AiProvider Provider { get; private set; } = null!;
    public ICollection<ApiKey> AllowedByKeys { get; private set; } = [];

    // Multi-tenant scoping (ENT-ARKANA-001)
    public Guid TenantId { get; private set; }
    public Tenant Tenant { get; private set; } = null!;

    private Model() { } // EF Core

    public static Model Create(Guid providerId, string name, string code,
        decimal costPerInput = 0, decimal costPerOutput = 0,
        int? maxTokens = null)
    {
        return new Model
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            Name = name,
            Code = code,
            IsEnabled = true,
            CostPerInputToken = costPerInput,
            CostPerOutputToken = costPerOutput,
            MaxTokensPerRequest = maxTokens,
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001")
        };
    }

    public void Enable() => IsEnabled = true;
    public void Disable() => IsEnabled = false;

    public void UpdateDetails(string name, string code,
        decimal costPerInput, decimal costPerOutput, int? maxTokens)
    {
        Name = name;
        Code = code;
        CostPerInputToken = costPerInput;
        CostPerOutputToken = costPerOutput;
        MaxTokensPerRequest = maxTokens;
    }
}
