using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Gateway.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Arkana.Gateway.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for agent definition CRUD and task execution.
/// </summary>
public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/agents").WithTags("Agents");

        // ── Agent CRUD ────────────────────────────────────────
        group.MapGet("/", async (ITenantProvider tenantProvider, IAgentRepository repo) =>
        {
            var tenantId = tenantProvider.TenantId
                ?? throw new InvalidOperationException("Tenant context is required.");
            return await ListAgents(tenantId, repo);
        })
            .WithName("ListAgents").WithOpenApi();

        group.MapGet("/{id:guid}", async (Guid id, IAgentRepository repo) =>
            await GetAgent(id, repo))
            .WithName("GetAgent").WithOpenApi();

        group.MapPost("/", async (CreateAgentRequest request, ITenantProvider tenantProvider, IAgentRepository repo) =>
            await CreateAgent(request, tenantProvider, repo))
            .WithName("CreateAgent").WithOpenApi();

        group.MapPut("/{id:guid}", async (Guid id, UpdateAgentRequest request, IAgentRepository repo) =>
            await UpdateAgent(id, request, repo))
            .WithName("UpdateAgent").WithOpenApi();

        group.MapDelete("/{id:guid}", async (Guid id, IAgentRepository repo) =>
            await DeleteAgent(id, repo))
            .WithName("DeleteAgent").WithOpenApi();

        // ── Task execution ────────────────────────────────────
        group.MapPost("/{id:guid}/run", async (
            Guid id,
            RunAgentRequest request,
            ITenantProvider tenantProvider,
            AgentOrchestrator orchestrator) =>
            await RunAgent(id, request, tenantProvider, orchestrator))
            .WithName("RunAgent").WithOpenApi();

        // ── Task queries ──────────────────────────────────────
        group.MapGet("/{id:guid}/tasks", async (Guid id, IAgentRepository repo) =>
            await ListAgentTasks(id, repo))
            .WithName("ListAgentTasks").WithOpenApi();

        group.MapGet("/tasks/{taskId:guid}", async (Guid taskId, IAgentRepository repo) =>
            await GetTask(taskId, repo))
            .WithName("GetAgentTask").WithOpenApi();

        // ── Delegation ────────────────────────────────────────
        group.MapPost("/tasks/{taskId:guid}/delegate", async (
            Guid taskId,
            DelegateTaskRequest request,
            AgentOrchestrator orchestrator) =>
            await DelegateTask(taskId, request, orchestrator))
            .WithName("DelegateAgentTask").WithOpenApi();
    }

    // ── Core logic (testable) ─────────────────────────────────

    internal static async Task<IResult> ListAgents(Guid tenantId, IAgentRepository repo)
    {
        var agents = await repo.GetAllAgentsAsync(tenantId);
        return Results.Ok(agents);
    }

    internal static async Task<IResult> GetAgent(Guid id, IAgentRepository repo)
    {
        var agent = await repo.GetAgentByIdAsync(id);
        return agent is not null
            ? Results.Ok(agent)
            : Results.NotFound(new { message = $"Agent '{id}' not found." });
    }

    internal static async Task<IResult> CreateAgent(
        CreateAgentRequest request,
        ITenantProvider tenantProvider,
        IAgentRepository repo)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new InvalidOperationException("Tenant context is required.");

        var existing = await repo.GetAgentByNameAsync(tenantId, request.Name);
        if (existing is not null)
            return Results.Conflict(new { message = $"Agent '{request.Name}' already exists for this tenant." });

        var agent = AgentDefinition.Create(
            tenantId,
            request.Name,
            request.Description ?? "",
            request.SystemPrompt,
            request.ModelCode,
            request.MaxTokens,
            request.Temperature,
            request.Metadata);

        await repo.AddAgentAsync(agent);

        return Results.Created($"/agents/{agent.Id}", new
        {
            agent.Id,
            agent.Name,
            message = "Agent created successfully."
        });
    }

    internal static async Task<IResult> UpdateAgent(Guid id, UpdateAgentRequest request, IAgentRepository repo)
    {
        var agent = await repo.GetAgentByIdAsync(id);
        if (agent is null)
            return Results.NotFound(new { message = "Agent not found." });

        if (request.Name is not null) agent.Name = request.Name;
        if (request.Description is not null) agent.Description = request.Description;
        if (request.SystemPrompt is not null) agent.SystemPrompt = request.SystemPrompt;
        if (request.ModelCode is not null) agent.ModelCode = request.ModelCode;
        if (request.MaxTokens.HasValue) agent.MaxTokens = request.MaxTokens.Value;
        if (request.Temperature.HasValue) agent.Temperature = request.Temperature.Value;
        if (request.IsActive.HasValue) agent.IsActive = request.IsActive.Value;
        if (request.Metadata is not null) agent.Metadata = request.Metadata;

        await repo.UpdateAgentAsync(agent);
        return Results.Ok(new { message = "Agent updated successfully." });
    }

    internal static async Task<IResult> DeleteAgent(Guid id, IAgentRepository repo)
    {
        var agent = await repo.GetAgentByIdAsync(id);
        if (agent is null)
            return Results.NotFound(new { message = "Agent not found." });

        await repo.DeleteAgentAsync(id);
        return Results.Ok(new { message = "Agent deleted." });
    }

    internal static async Task<IResult> RunAgent(
        Guid id,
        RunAgentRequest request,
        ITenantProvider tenantProvider,
        AgentOrchestrator orchestrator)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new InvalidOperationException("Tenant context is required.");

        var task = await orchestrator.RunAsync(
            id,
            tenantId,
            request.Input,
            request.ParentTaskId);

        return task.Status switch
        {
            AgentTaskStatus.Completed => Results.Ok(new
            {
                task.Id,
                task.Status,
                task.Output,
                task.TokenUsed,
                task.DurationMs
            }),
            AgentTaskStatus.Failed => Results.Json(new
            {
                task.Id,
                task.Status,
                task.ErrorMessage,
                task.DurationMs
            }, statusCode: 502),
            _ => Results.Ok(new
            {
                task.Id,
                task.Status,
                task.DurationMs
            })
        };
    }

    internal static async Task<IResult> ListAgentTasks(Guid agentId, IAgentRepository repo)
    {
        var tasks = await repo.GetTasksByAgentAsync(agentId);
        return Results.Ok(tasks);
    }

    internal static async Task<IResult> GetTask(Guid taskId, IAgentRepository repo)
    {
        var task = await repo.GetTaskByIdAsync(taskId);
        return task is not null
            ? Results.Ok(task)
            : Results.NotFound(new { message = $"Task '{taskId}' not found." });
    }

    internal static async Task<IResult> DelegateTask(
        Guid taskId,
        DelegateTaskRequest request,
        AgentOrchestrator orchestrator)
    {
        var childTask = await orchestrator.DelegateAsync(
            taskId,
            request.ChildAgentId,
            request.AdditionalInput);

        return childTask.Status switch
        {
            AgentTaskStatus.Completed => Results.Ok(new
            {
                childTask.Id,
                childTask.Status,
                childTask.Output,
                childTask.TokenUsed,
                childTask.DurationMs,
                ParentTaskId = taskId
            }),
            AgentTaskStatus.Failed => Results.Json(new
            {
                childTask.Id,
                childTask.Status,
                childTask.ErrorMessage,
                childTask.DurationMs,
                ParentTaskId = taskId
            }, statusCode: 502),
            _ => Results.Ok(new
            {
                childTask.Id,
                childTask.Status,
                childTask.DurationMs,
                ParentTaskId = taskId
            })
        };
    }
}

// ── Request DTOs ───────────────────────────────────────────

public sealed record CreateAgentRequest(
    string Name,
    string? Description,
    string SystemPrompt,
    string ModelCode,
    int MaxTokens = 4096,
    decimal Temperature = 0.7m,
    string? Metadata = null);

public sealed record UpdateAgentRequest(
    string? Name,
    string? Description,
    string? SystemPrompt,
    string? ModelCode,
    int? MaxTokens,
    decimal? Temperature,
    bool? IsActive,
    string? Metadata);

public sealed record RunAgentRequest(
    string Input,
    Guid? ParentTaskId = null);

public sealed record DelegateTaskRequest(
    Guid ChildAgentId,
    string AdditionalInput);
