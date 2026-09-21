using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Gateway.Api.Endpoints;
using Arkana.Gateway.Api.Services;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Arkana.Gateway.Api.Tests.Endpoints;

public sealed class AgentEndpointsTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static GatewayDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase($"agent_endpoint_test_{Guid.NewGuid()}")
            .Options;
        return new GatewayDbContext(opts);
    }

    private static AgentRepository Repo(GatewayDbContext ctx) => new(ctx);

    private static ITenantProvider FakeTenant(Guid tenantId)
    {
        var tp = Substitute.For<ITenantProvider>();
        tp.TenantId.Returns(tenantId);
        return tp;
    }

    private static int GetStatusCode(IResult result)
    {
        var statusCodeProp = result.GetType().GetProperty("StatusCode");
        if (statusCodeProp is not null)
            return (int)statusCodeProp.GetValue(result)!;
        if (result is NotFound<object>) return 404;
        if (result is Conflict<object>) return 409;
        if (result is BadRequest<object>) return 400;
        return 0;
    }

    // ── Agent CRUD Tests ─────────────────────────────────────

    [Fact]
    public async Task ListAgents_ReturnsOk()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        await repo.AddAgentAsync(AgentDefinition.Create(TenantId, "Agent1", "desc", "You are helpful.", "deepseek-v4-flash"));

        var result = await AgentEndpoints.ListAgents(TenantId, repo);
        GetStatusCode(result).Should().Be(200);
    }

    [Fact]
    public async Task GetAgent_Existing_ReturnsOk()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var agent = AgentDefinition.Create(TenantId, "Agent1", "desc", "You are helpful.", "deepseek-v4-flash");
        await repo.AddAgentAsync(agent);

        var result = await AgentEndpoints.GetAgent(agent.Id, repo);
        GetStatusCode(result).Should().Be(200);
    }

    [Fact]
    public async Task GetAgent_NotFound_Returns404()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);

        var result = await AgentEndpoints.GetAgent(Guid.NewGuid(), repo);
        GetStatusCode(result).Should().Be(404);
    }

    [Fact]
    public async Task CreateAgent_Valid_Returns201()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var tenantProvider = FakeTenant(TenantId);
        var req = new CreateAgentRequest("MyAgent", "A test agent", "You are helpful.", "deepseek-v4-flash");

        var result = await AgentEndpoints.CreateAgent(req, tenantProvider, repo);
        GetStatusCode(result).Should().Be(201);

        var stored = await repo.GetAgentByNameAsync(TenantId, "MyAgent");
        stored.Should().NotBeNull();
        stored!.Name.Should().Be("MyAgent");
        stored.ModelCode.Should().Be("deepseek-v4-flash");
    }

    [Fact]
    public async Task CreateAgent_DuplicateName_Returns409()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var tenantProvider = FakeTenant(TenantId);
        await repo.AddAgentAsync(AgentDefinition.Create(TenantId, "Agent1", "desc", "prompt", "deepseek-v4-flash"));

        var req = new CreateAgentRequest("Agent1", "desc", "prompt", "deepseek-v4-flash");
        var result = await AgentEndpoints.CreateAgent(req, tenantProvider, repo);
        GetStatusCode(result).Should().Be(409);
    }

    [Fact]
    public async Task UpdateAgent_Existing_Returns200()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var agent = AgentDefinition.Create(TenantId, "Agent1", "old desc", "old prompt", "deepseek-v4-flash");
        await repo.AddAgentAsync(agent);

        var req = new UpdateAgentRequest(Name: "Agent1Updated", Description: "new desc", SystemPrompt: null, ModelCode: null, MaxTokens: null, Temperature: null, IsActive: null, Metadata: null);
        var result = await AgentEndpoints.UpdateAgent(agent.Id, req, repo);
        GetStatusCode(result).Should().Be(200);

        var updated = await repo.GetAgentByIdAsync(agent.Id);
        updated!.Name.Should().Be("Agent1Updated");
        updated.Description.Should().Be("new desc");
    }

    [Fact]
    public async Task UpdateAgent_NotFound_Returns404()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var req = new UpdateAgentRequest(null, null, null, null, null, null, null, null);

        var result = await AgentEndpoints.UpdateAgent(Guid.NewGuid(), req, repo);
        GetStatusCode(result).Should().Be(404);
    }

    [Fact]
    public async Task DeleteAgent_Existing_Returns200()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var agent = AgentDefinition.Create(TenantId, "Agent1", "desc", "prompt", "deepseek-v4-flash");
        await repo.AddAgentAsync(agent);

        var result = await AgentEndpoints.DeleteAgent(agent.Id, repo);
        GetStatusCode(result).Should().Be(200);

        var deleted = await repo.GetAgentByIdAsync(agent.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAgent_NotFound_Returns404()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);

        var result = await AgentEndpoints.DeleteAgent(Guid.NewGuid(), repo);
        GetStatusCode(result).Should().Be(404);
    }

    // ── Task Query Tests ─────────────────────────────────────

    [Fact]
    public async Task ListAgentTasks_ReturnsOk()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var agent = AgentDefinition.Create(TenantId, "Agent1", "desc", "prompt", "deepseek-v4-flash");
        await repo.AddAgentAsync(agent);
        await repo.AddTaskAsync(AgentTask.Create(agent.Id, TenantId, "test input"));

        var result = await AgentEndpoints.ListAgentTasks(agent.Id, repo);
        GetStatusCode(result).Should().Be(200);
    }

    [Fact]
    public async Task GetTask_Existing_ReturnsOk()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var agent = AgentDefinition.Create(TenantId, "Agent1", "desc", "prompt", "deepseek-v4-flash");
        await repo.AddAgentAsync(agent);
        var task = AgentTask.Create(agent.Id, TenantId, "test input");
        await repo.AddTaskAsync(task);

        var result = await AgentEndpoints.GetTask(task.Id, repo);
        GetStatusCode(result).Should().Be(200);
    }

    [Fact]
    public async Task GetTask_NotFound_Returns404()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);

        var result = await AgentEndpoints.GetTask(Guid.NewGuid(), repo);
        GetStatusCode(result).Should().Be(404);
    }

    // ── Agent Definition Defaults ────────────────────────────

    [Fact]
    public void AgentDefinition_Create_SetsDefaults()
    {
        var agent = AgentDefinition.Create(TenantId, "Agent1", "desc", "prompt", "deepseek-v4-flash");

        agent.Id.Should().NotBe(Guid.Empty);
        agent.TenantId.Should().Be(TenantId);
        agent.Name.Should().Be("Agent1");
        agent.Description.Should().Be("desc");
        agent.SystemPrompt.Should().Be("prompt");
        agent.ModelCode.Should().Be("deepseek-v4-flash");
        agent.MaxTokens.Should().Be(4096);
        agent.Temperature.Should().Be(0.7m);
        agent.IsActive.Should().BeTrue();
        agent.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AgentDefinition_Create_ThrowsOnEmptyName()
    {
        var act = () => AgentDefinition.Create(TenantId, "", "desc", "prompt", "model");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AgentDefinition_Create_ThrowsOnEmptyPrompt()
    {
        var act = () => AgentDefinition.Create(TenantId, "Agent1", "desc", "", "model");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AgentDefinition_Create_ThrowsOnEmptyModelCode()
    {
        var act = () => AgentDefinition.Create(TenantId, "Agent1", "desc", "prompt", "");
        act.Should().Throw<ArgumentException>();
    }

    // ── AgentTask State Transitions ──────────────────────────

    [Fact]
    public void AgentTask_Create_SetsPendingStatus()
    {
        var task = AgentTask.Create(Guid.NewGuid(), TenantId, "hello");

        task.Status.Should().Be(AgentTaskStatus.Pending);
        task.Id.Should().NotBe(Guid.Empty);
        task.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AgentTask_MarkRunning_SetsRunningStatus()
    {
        var task = AgentTask.Create(Guid.NewGuid(), TenantId, "hello");
        task.MarkRunning();

        task.Status.Should().Be(AgentTaskStatus.Running);
        task.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public void AgentTask_MarkCompleted_SetsCompletedStatus()
    {
        var task = AgentTask.Create(Guid.NewGuid(), TenantId, "hello");
        task.MarkRunning();
        task.MarkCompleted("result", 100);

        task.Status.Should().Be(AgentTaskStatus.Completed);
        task.Output.Should().Be("result");
        task.TokenUsed.Should().Be(100);
        task.CompletedAt.Should().NotBeNull();
        task.DurationMs.Should().NotBeNull();
    }

    [Fact]
    public void AgentTask_MarkFailed_SetsFailedStatus()
    {
        var task = AgentTask.Create(Guid.NewGuid(), TenantId, "hello");
        task.MarkRunning();
        task.MarkFailed("something broke");

        task.Status.Should().Be(AgentTaskStatus.Failed);
        task.ErrorMessage.Should().Be("something broke");
        task.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void AgentTask_Cancel_SetsCancelledStatus()
    {
        var task = AgentTask.Create(Guid.NewGuid(), TenantId, "hello");
        task.MarkRunning();
        task.Cancel();

        task.Status.Should().Be(AgentTaskStatus.Cancelled);
        task.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void AgentTask_Create_ThrowsOnEmptyInput()
    {
        var act = () => AgentTask.Create(Guid.NewGuid(), TenantId, "");
        act.Should().Throw<ArgumentException>();
    }

    // ── Repository Tests ─────────────────────────────────────

    [Fact]
    public async Task AgentRepository_GetByTenant_Isolation()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);

        var tenant1 = Guid.NewGuid();
        var tenant2 = Guid.NewGuid();

        await repo.AddAgentAsync(AgentDefinition.Create(tenant1, "Agent1", "desc", "prompt", "model"));
        await repo.AddAgentAsync(AgentDefinition.Create(tenant2, "Agent2", "desc", "prompt", "model"));

        var t1Agents = await repo.GetAllAgentsAsync(tenant1);
        t1Agents.Should().HaveCount(1);
        t1Agents[0].Name.Should().Be("Agent1");

        var t2Agents = await repo.GetAllAgentsAsync(tenant2);
        t2Agents.Should().HaveCount(1);
        t2Agents[0].Name.Should().Be("Agent2");
    }

    [Fact]
    public async Task AgentRepository_TaskNavigation()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);

        var agent = AgentDefinition.Create(TenantId, "Agent1", "desc", "prompt", "model");
        await repo.AddAgentAsync(agent);

        var task = AgentTask.Create(agent.Id, TenantId, "input");
        await repo.AddTaskAsync(task);

        var tasks = await repo.GetTasksByAgentAsync(agent.Id);
        tasks.Should().HaveCount(1);
        tasks[0].AgentId.Should().Be(agent.Id);
    }
}
