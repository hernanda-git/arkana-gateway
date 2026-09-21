using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Repository for agent definitions and agent tasks.
/// Scoped per-request via DI.
/// </summary>
public interface IAgentRepository
{
    // ── AgentDefinition CRUD ──────────────────────────────────
    Task<List<AgentDefinition>> GetAllAgentsAsync(Guid tenantId, CancellationToken ct = default);
    Task<AgentDefinition?> GetAgentByIdAsync(Guid id, CancellationToken ct = default);
    Task<AgentDefinition?> GetAgentByNameAsync(Guid tenantId, string name, CancellationToken ct = default);
    Task AddAgentAsync(AgentDefinition agent, CancellationToken ct = default);
    Task UpdateAgentAsync(AgentDefinition agent, CancellationToken ct = default);
    Task DeleteAgentAsync(Guid id, CancellationToken ct = default);

    // ── AgentTask CRUD ────────────────────────────────────────
    Task<AgentTask?> GetTaskByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<AgentTask>> GetTasksByAgentAsync(Guid agentId, CancellationToken ct = default);
    Task<List<AgentTask>> GetTasksByParentAsync(Guid parentTaskId, CancellationToken ct = default);
    Task AddTaskAsync(AgentTask task, CancellationToken ct = default);
    Task UpdateTaskAsync(AgentTask task, CancellationToken ct = default);
}
