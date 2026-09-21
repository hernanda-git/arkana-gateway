using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAgentRepository"/>.
/// Manages agent definitions and agent tasks in PostgreSQL.
/// </summary>
public sealed class AgentRepository : IAgentRepository
{
    private readonly GatewayDbContext _db;
    public AgentRepository(GatewayDbContext db) => _db = db;

    // ── AgentDefinition ──────────────────────────────────────

    public Task<List<AgentDefinition>> GetAllAgentsAsync(Guid tenantId, CancellationToken ct = default)
        => _db.AgentDefinitions
            .Where(a => a.TenantId == tenantId)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);

    public Task<AgentDefinition?> GetAgentByIdAsync(Guid id, CancellationToken ct = default)
        => _db.AgentDefinitions.FindAsync([id], ct).AsTask();

    public Task<AgentDefinition?> GetAgentByNameAsync(Guid tenantId, string name, CancellationToken ct = default)
        => _db.AgentDefinitions
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Name == name, ct);

    public async Task AddAgentAsync(AgentDefinition agent, CancellationToken ct = default)
    {
        _db.AgentDefinitions.Add(agent);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAgentAsync(AgentDefinition agent, CancellationToken ct = default)
    {
        _db.AgentDefinitions.Update(agent);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAgentAsync(Guid id, CancellationToken ct = default)
    {
        var agent = await _db.AgentDefinitions.FindAsync([id], ct);
        if (agent is not null)
        {
            _db.AgentDefinitions.Remove(agent);
            await _db.SaveChangesAsync(ct);
        }
    }

    // ── AgentTask ────────────────────────────────────────────

    public Task<AgentTask?> GetTaskByIdAsync(Guid id, CancellationToken ct = default)
        => _db.AgentTasks.FindAsync([id], ct).AsTask();

    public Task<List<AgentTask>> GetTasksByAgentAsync(Guid agentId, CancellationToken ct = default)
        => _db.AgentTasks
            .Where(t => t.AgentId == agentId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

    public Task<List<AgentTask>> GetTasksByParentAsync(Guid parentTaskId, CancellationToken ct = default)
        => _db.AgentTasks
            .Where(t => t.ParentTaskId == parentTaskId)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);

    public async Task AddTaskAsync(AgentTask task, CancellationToken ct = default)
    {
        _db.AgentTasks.Add(task);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateTaskAsync(AgentTask task, CancellationToken ct = default)
    {
        _db.AgentTasks.Update(task);
        await _db.SaveChangesAsync(ct);
    }
}
