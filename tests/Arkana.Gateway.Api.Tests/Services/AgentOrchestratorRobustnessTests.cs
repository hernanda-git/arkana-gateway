using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Gateway.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Arkana.Gateway.Api.Tests.Services;

/// <summary>
/// Regression tests for AgentOrchestrator robustness: bounded retry on
/// transient upstream failures, fail-fast on deterministic failures, and
/// delegation depth/cycle guards for A2A chains.
/// </summary>
public sealed class AgentOrchestratorRobustnessTests
{
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly IAgentRepository _agentRepo = Substitute.For<IAgentRepository>();
    private readonly IModelRepository _modelRepo = Substitute.For<IModelRepository>();
    private readonly IAiProviderRepository _providerRepo = Substitute.For<IAiProviderRepository>();
    private readonly IProviderConnectorFactory _connectorFactory = Substitute.For<IProviderConnectorFactory>();

    private AgentOrchestrator CreateSut() => new(
        _agentRepo, _modelRepo, _providerRepo, _connectorFactory,
        Substitute.For<ILogger<AgentOrchestrator>>());

    private static (AgentDefinition Agent, Model Model) SetupModelGraph(string modelCode)
    {
        var provider = AiProvider.Create("Test Provider", "test-provider", 0);
        var model = Model.Create(provider.Id, "Test Model", modelCode);

        // Provider navigation has a private setter; populate via reflection
        // so the orchestrator's `model.Provider is null` guard passes.
        typeof(Model).GetProperty(nameof(Model.Provider))!.SetValue(model, provider);
        return (AgentDefinition.Create(TenantId, "OrchAgent", "desc", "system prompt", modelCode), model);
    }

    private AgentDefinition SetupAgent()
    {
        var (agent, model) = SetupModelGraph("test-model");
        _agentRepo.GetAgentByIdAsync(agent.Id, Arg.Any<CancellationToken>()).Returns(agent);
        _modelRepo.GetByCodeAsync("test-model", TenantId, Arg.Any<CancellationToken>()).Returns(model);
        return agent;
    }

    private static ChatResult OkResult() => new()
    {
        Content = "done", InputTokens = 10, OutputTokens = 5, Model = "test-model"
    };

    private static ChatResult FailResult(int? status) => new()
    {
        Content = string.Empty, ErrorMessage = "upstream failure", UpstreamStatus = status
    };

    [Fact]
    public async Task RunAsync_TransientFailure_IsRetriedAndSucceeds()
    {
        var agent = SetupAgent();
        var calls = 0;
        _connectorFactory.ResolveAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>())
            .Returns(new FakeConnector(_ => ++calls == 1 ? FailResult(503) : OkResult()));

        var sut = CreateSut();
        var task = await sut.RunAsync(agent.Id, TenantId, "hello");

        task.Status.Should().Be(AgentTaskStatus.Completed);
        calls.Should().Be(2);
        task.Output.Should().Be("done");
    }

    [Fact]
    public async Task RunAsync_DeterministicFailure_DoesNotRetry()
    {
        var agent = SetupAgent();
        var calls = 0;
        _connectorFactory.ResolveAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>())
            .Returns(new FakeConnector(_ => { calls++; return FailResult(400); }));

        var sut = CreateSut();
        var task = await sut.RunAsync(agent.Id, TenantId, "hello");

        task.Status.Should().Be(AgentTaskStatus.Failed);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_AllAttemptsTransient_FailsAfterMaxAttempts()
    {
        var agent = SetupAgent();
        var calls = 0;
        _connectorFactory.ResolveAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>())
            .Returns(new FakeConnector(_ => { calls++; return FailResult(429); }));

        var sut = CreateSut();
        var task = await sut.RunAsync(agent.Id, TenantId, "hello");

        task.Status.Should().Be(AgentTaskStatus.Failed);
        calls.Should().Be(AgentOrchestrator.MaxAttempts);
    }

    [Fact]
    public async Task DelegateAsync_CycleTarget_Rejects()
    {
        var (agentA, _) = SetupModelGraph("m");
        var agentB = AgentDefinition.Create(TenantId, "AgentB", "d", "p", "m");
        _agentRepo.GetAgentByIdAsync(agentA.Id, Arg.Any<CancellationToken>()).Returns(agentA);
        _agentRepo.GetAgentByIdAsync(agentB.Id, Arg.Any<CancellationToken>()).Returns(agentB);
        _modelRepo.GetByCodeAsync("m", TenantId, Arg.Any<CancellationToken>())
            .Returns(SetupModelGraph("m").Model);

        var parentTask = AgentTask.Create(agentA.Id, TenantId, "root input");
        _agentRepo.GetTaskByIdAsync(parentTask.Id, Arg.Any<CancellationToken>()).Returns(parentTask);

        var sut = CreateSut();
        // Delegating back to Agent A (already in the chain) must be rejected.
        var act = () => sut.DelegateAsync(parentTask.Id, agentA.Id, "continue");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cycle*");
    }

    [Fact]
    public async Task DelegateAsync_ExceedsMaxDepth_Rejects()
    {
        var agents = Enumerable.Range(0, AgentOrchestrator.MaxDelegationDepth + 2)
            .Select(i => AgentDefinition.Create(TenantId, $"Agent{i}", "d", "p", "m"))
            .ToList();
        foreach (var a in agents)
            _agentRepo.GetAgentByIdAsync(a.Id, Arg.Any<CancellationToken>()).Returns(a);
        _modelRepo.GetByCodeAsync("m", TenantId, Arg.Any<CancellationToken>())
            .Returns(SetupModelGraph("m").Model);

        // Build a parent chain whose walked depth already hits the cap.
        AgentTask? chain = null;
        for (var i = 0; i <= AgentOrchestrator.MaxDelegationDepth; i++)
        {
            chain = chain is null
                ? AgentTask.Create(agents[i].Id, TenantId, "root")
                : AgentTask.Create(agents[i].Id, TenantId, "child", chain.Id);
            var current = chain;
            _agentRepo.GetTaskByIdAsync(current.Id, Arg.Any<CancellationToken>()).Returns(current);
        }

        var sut = CreateSut();
        // One more delegation from the tip exceeds the limit.
        var act = () => sut.DelegateAsync(chain!.Id, agents[^1].Id, "one more");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*depth*");
    }

    private sealed class FakeConnector(Func<ChatRequest, ChatResult> responder) : IChatCompletionService
    {
        public string ProviderName => "fake";
        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
            => Task.FromResult(responder(request));
    }
}
