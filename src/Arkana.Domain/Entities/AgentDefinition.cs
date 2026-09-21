namespace Arkana.Domain.Entities;

/// <summary>
/// Defines an AI agent with its model configuration, system prompt, and parameters.
/// Agents are tenant-scoped and can be enabled/disabled per tenant.
/// </summary>
public sealed class AgentDefinition
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string ModelCode { get; set; } = string.Empty;
    public int MaxTokens { get; set; } = 4096;
    public decimal Temperature { get; set; } = 0.7m;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optional JSON metadata for agent configuration extensions.</summary>
    public string? Metadata { get; set; }

    /// <summary>Navigation: tasks executed by this agent.</summary>
    public ICollection<AgentTask> Tasks { get; set; } = new List<AgentTask>();

    private AgentDefinition() { } // EF Core

    public static AgentDefinition Create(
        Guid tenantId,
        string name,
        string description,
        string systemPrompt,
        string modelCode,
        int maxTokens = 4096,
        decimal temperature = 0.7m,
        string? metadata = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(systemPrompt);
        ArgumentException.ThrowIfNullOrEmpty(modelCode);

        return new AgentDefinition
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Description = description,
            SystemPrompt = systemPrompt,
            ModelCode = modelCode,
            MaxTokens = maxTokens,
            Temperature = temperature,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            Metadata = metadata
        };
    }
}
