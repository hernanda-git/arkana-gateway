namespace Arkana.Domain.Entities;

/// <summary>
/// Represents a single execution of an agent. Tracks input, output,
/// status, token usage, and supports parent-child task chains for
/// multi-step delegation workflows.
/// </summary>
public sealed class AgentTask
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid TenantId { get; set; }
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Pending;
    public string Input { get; set; } = string.Empty;
    public string? Output { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? DurationMs { get; set; }
    public int? TokenUsed { get; set; }
    public Guid? ParentTaskId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Navigation: the agent that owns this task.</summary>
    public AgentDefinition Agent { get; set; } = null!;

    private AgentTask() { } // EF Core

    public static AgentTask Create(
        Guid agentId,
        Guid tenantId,
        string input,
        Guid? parentTaskId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(input);

        return new AgentTask
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            TenantId = tenantId,
            Status = AgentTaskStatus.Pending,
            Input = input,
            ParentTaskId = parentTaskId,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public void MarkRunning()
    {
        Status = AgentTaskStatus.Running;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public void MarkCompleted(string output, int? tokensUsed)
    {
        Status = AgentTaskStatus.Completed;
        Output = output;
        TokenUsed = tokensUsed;
        CompletedAt = DateTimeOffset.UtcNow;
        DurationMs = StartedAt.HasValue
            ? (long)(CompletedAt.Value - StartedAt.Value).TotalMilliseconds
            : null;
    }

    public void MarkFailed(string errorMessage)
    {
        Status = AgentTaskStatus.Failed;
        ErrorMessage = errorMessage;
        CompletedAt = DateTimeOffset.UtcNow;
        DurationMs = StartedAt.HasValue
            ? (long)(CompletedAt.Value - StartedAt.Value).TotalMilliseconds
            : null;
    }

    public void Cancel()
    {
        Status = AgentTaskStatus.Cancelled;
        CompletedAt = DateTimeOffset.UtcNow;
        DurationMs = StartedAt.HasValue
            ? (long)(CompletedAt.Value - StartedAt.Value).TotalMilliseconds
            : null;
    }
}
