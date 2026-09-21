namespace Arkana.Domain.Entities;

/// <summary>Lifecycle states for an agent task.</summary>
public enum AgentTaskStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}
