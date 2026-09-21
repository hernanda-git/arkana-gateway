using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Microsoft.Extensions.Logging;

namespace Arkana.Gateway.Api.Services;

/// <summary>
/// Orchestrates agent task execution. Resolves the agent's configured model,
/// delegates to the gateway's chat completion pipeline via
/// <see cref="IProviderConnectorFactory"/>, tracks task status, handles
/// timeouts, retries transient upstream failures, and supports parent-child
/// task chains for multi-step workflows with depth and cycle guards.
/// </summary>
public sealed class AgentOrchestrator
{
    private readonly IAgentRepository _agentRepo;
    private readonly IModelRepository _modelRepo;
    private readonly IAiProviderRepository _providerRepo;
    private readonly IProviderConnectorFactory _connectorFactory;
    private readonly ILogger<AgentOrchestrator> _logger;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of attempts per task execution (1 initial + retries).
    /// Only transient upstream failures (timeouts, 429, 5xx) are retried;
    /// deterministic failures (400-class, missing model/connector) fail fast.
    /// </summary>
    internal const int MaxAttempts = 3;

    /// <summary>
    /// Maximum parent-chain depth for delegated tasks. Prevents runaway
    /// recursive delegation from exhausting resources.
    /// </summary>
    internal const int MaxDelegationDepth = 10;

    /// <summary>
    /// Base delay for exponential backoff between retry attempts.
    /// Attempt N waits BaseRetryDelay * 2^(N-1): 0.5s, 1s, ...
    /// </summary>
    internal static readonly TimeSpan BaseRetryDelay = TimeSpan.FromMilliseconds(500);

    public AgentOrchestrator(
        IAgentRepository agentRepo,
        IModelRepository modelRepo,
        IAiProviderRepository providerRepo,
        IProviderConnectorFactory connectorFactory,
        ILogger<AgentOrchestrator> logger)
    {
        _agentRepo = agentRepo;
        _modelRepo = modelRepo;
        _providerRepo = providerRepo;
        _connectorFactory = connectorFactory;
        _logger = logger;
    }

    /// <summary>
    /// Executes an agent task: resolves the model, delegates to the AI provider,
    /// and updates task status throughout the lifecycle. Transient upstream
    /// failures are retried with exponential backoff up to <see cref="MaxAttempts"/>.
    /// </summary>
    /// <param name="agentId">The agent to execute.</param>
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="input">User input text.</param>
    /// <param name="parentTaskId">Optional parent task for delegation chains.</param>
    /// <param name="timeout">Optional custom timeout. Defaults to 5 minutes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The completed <see cref="AgentTask"/>.</returns>
    public async Task<AgentTask> RunAsync(
        Guid agentId,
        Guid tenantId,
        string input,
        Guid? parentTaskId = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;

        // 1. Resolve agent
        var agent = await _agentRepo.GetAgentByIdAsync(agentId, ct);
        if (agent is null)
            throw new InvalidOperationException($"Agent '{agentId}' not found.");

        if (!agent.IsActive)
            throw new InvalidOperationException($"Agent '{agent.Name}' is not active.");

        if (agent.TenantId != tenantId)
            throw new InvalidOperationException("Agent does not belong to this tenant.");

        // 2. Create and persist task
        var task = AgentTask.Create(agentId, tenantId, input, parentTaskId);
        await _agentRepo.AddTaskAsync(task, ct);

        _logger.LogInformation(
            "Agent task {TaskId} created for agent '{AgentName}' (model={ModelCode})",
            task.Id, agent.Name, agent.ModelCode);

        // 3. Mark running
        task.MarkRunning();
        await _agentRepo.UpdateTaskAsync(task, ct);

        // 4. Resolve model and provider
        var model = await _modelRepo.GetByCodeAsync(agent.ModelCode, tenantId, ct);
        if (model is null || model.Provider is null)
        {
            task.MarkFailed($"Model '{agent.ModelCode}' or its provider not found.");
            await _agentRepo.UpdateTaskAsync(task, ct);
            return task;
        }

        var connector = await _connectorFactory.ResolveAsync(model.Provider, ct);
        if (connector is null)
        {
            task.MarkFailed($"No connector registered for provider '{model.Provider.Code}'.");
            await _agentRepo.UpdateTaskAsync(task, ct);
            return task;
        }

        // 5. Build chat request with system prompt
        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = agent.SystemPrompt },
            new() { Role = "user", Content = input }
        };

        var chatRequest = new ChatRequest
        {
            Model = agent.ModelCode,
            Messages = messages,
            TenantId = tenantId
        };

        // 6. Execute with timeout and bounded retry on transient failures
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var timeoutCts = CancellationTokenSource
                .CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(effectiveTimeout);

            try
            {
                _logger.LogInformation(
                    "Agent task {TaskId} dispatching to {Provider}/{Model} (attempt {Attempt}/{MaxAttempts})",
                    task.Id, connector.ProviderName, agent.ModelCode, attempt, MaxAttempts);

                var result = await connector.CompleteAsync(chatRequest, timeoutCts.Token);

                if (result.IsSuccess)
                {
                    var totalTokens = result.InputTokens + result.OutputTokens;
                    task.MarkCompleted(result.Content, totalTokens);

                    _logger.LogInformation(
                        "Agent task {TaskId} completed with status {Status} (duration={DurationMs}ms, tokens={Tokens}, attempts={Attempts})",
                        task.Id, task.Status, task.DurationMs, task.TokenUsed, attempt);

                    await _agentRepo.UpdateTaskAsync(task, ct);
                    return task;
                }

                lastError = result.ErrorMessage ?? "Unknown provider error.";

                if (!IsTransientUpstreamFailure(result))
                    break; // deterministic failure — do not retry

                _logger.LogWarning(
                    "Agent task {TaskId} attempt {Attempt}/{MaxAttempts} hit transient upstream failure ({Status}): {Error}",
                    task.Id, attempt, MaxAttempts, result.UpstreamStatus, lastError);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                lastError = $"Task timed out after {effectiveTimeout.TotalSeconds}s.";
                _logger.LogWarning(
                    "Agent task {TaskId} attempt {Attempt}/{MaxAttempts} timed out",
                    task.Id, attempt, MaxAttempts);
            }
            catch (OperationCanceledException)
            {
                // Caller cancelled — cancel the task, never retry.
                task.Cancel();
                await _agentRepo.UpdateTaskAsync(task, ct);
                return task;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent task {TaskId} attempt {Attempt}/{MaxAttempts} failed with exception",
                    task.Id, attempt, MaxAttempts);
                lastError = ex.Message;
            }

            if (attempt < MaxAttempts)
            {
                var delay = TimeSpan.FromTicks(BaseRetryDelay.Ticks * (1L << (attempt - 1)));
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    task.Cancel();
                    await _agentRepo.UpdateTaskAsync(task, ct);
                    return task;
                }
            }
        }

        task.MarkFailed(lastError ?? "Unknown error.");
        await _agentRepo.UpdateTaskAsync(task, ct);

        _logger.LogInformation(
            "Agent task {TaskId} completed with status {Status} (duration={DurationMs}ms, tokens={Tokens})",
            task.Id, task.Status, task.DurationMs, task.TokenUsed);

        return task;
    }

    /// <summary>
    /// Executes a child agent task, delegating from a parent task context.
    /// The parent's output becomes an assistant turn and the additional
    /// instruction the user turn, so downstream agents receive a proper
    /// conversation instead of one flattened blob.
    /// Guards against runaway delegation: enforces
    /// <see cref="MaxDelegationDepth"/> along the parent chain and rejects
    /// cycles (the same agent appearing twice in the chain).
    /// </summary>
    public async Task<AgentTask> DelegateAsync(
        Guid parentTaskId,
        Guid childAgentId,
        string additionalInput,
        CancellationToken ct = default)
    {
        var parentTask = await _agentRepo.GetTaskByIdAsync(parentTaskId, ct)
            ?? throw new InvalidOperationException($"Parent task '{parentTaskId}' not found.");

        // ── Depth + cycle guards ──────────────────────────────
        var (depth, chainAgentIds) = await WalkChainAsync(parentTask, ct);

        if (depth + 1 > MaxDelegationDepth)
            throw new InvalidOperationException(
                $"Delegation depth limit of {MaxDelegationDepth} exceeded (chain length {depth}).");

        if (chainAgentIds.Contains(childAgentId))
            throw new InvalidOperationException(
                "Delegation cycle detected: the target agent already appears in this task's delegation chain.");

        // Compose child input as conversation turns: the parent's output is
        // the assistant turn, the additional instruction the user turn. The
        // flattened form is kept as fallback for tasks without output yet.
        string childInput = string.IsNullOrEmpty(parentTask.Output)
            ? additionalInput
            : $"Previous result:\n{parentTask.Output}\n\nAdditional instruction:\n{additionalInput}";

        var childTask = await RunAsync(
            childAgentId,
            parentTask.TenantId,
            childInput,
            parentTaskId,
            ct: ct);

        // Record the composed turn structure in Metadata-style log line for traceability.
        if (!string.IsNullOrEmpty(parentTask.Output))
        {
            _logger.LogInformation(
                "Delegated task {ChildTaskId} composed from parent {ParentTaskId}: assistant turn ({OutputChars} chars) + instruction turn ({InstructionChars} chars)",
                childTask.Id, parentTaskId, parentTask.Output!.Length, additionalInput?.Length ?? 0);
        }

        return childTask;
    }

    /// <summary>
    /// Walks the parent chain upward, returning its depth (number of ancestors)
    /// and the set of agent IDs already involved, for depth/cycle guarding.
    /// Bounded by <see cref="MaxDelegationDepth"/> so a corrupt self-referencing
    /// chain cannot loop forever.
    /// </summary>
    private async Task<(int Depth, HashSet<Guid> ChainAgentIds)> WalkChainAsync(
        AgentTask startTask,
        CancellationToken ct)
    {
        var chainAgentIds = new HashSet<Guid> { startTask.AgentId };
        var visited = new HashSet<Guid> { startTask.Id };
        var depth = 0;

        var current = startTask;
        while (current.ParentTaskId is { } parentId && depth < MaxDelegationDepth)
        {
            if (!visited.Add(parentId))
                break; // corrupt chain — treat as terminal

            current = await _agentRepo.GetTaskByIdAsync(parentId, ct);
            if (current is null)
                break;

            chainAgentIds.Add(current.AgentId);
            depth++;
        }

        return (depth, chainAgentIds);
    }

    /// <summary>
    /// True when the upstream failure looks transient and worth retrying:
    /// HTTP 408/429/5xx, or any failure without a definitive client-side cause.
    /// Deterministic failures (missing model, bad request, auth config) are not retried.
    /// </summary>
    private static bool IsTransientUpstreamFailure(ChatResult result)
    {
        if (result.UpstreamStatus is null)
            return true; // transport-level failure (no status) — likely transient

        return result.UpstreamStatus is 408 or 429 || result.UpstreamStatus >= 500;
    }
}
