using Arkana.Domain.Models;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Service for interacting with n8n workflow automation via its REST API.
/// </summary>
public interface IN8nService
{
    /// <summary>
    /// Lists all workflows from n8n.
    /// </summary>
    Task<IReadOnlyList<WorkflowInfo>> GetWorkflowsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets detailed information about a specific workflow.
    /// </summary>
    Task<WorkflowInfo?> GetWorkflowAsync(string workflowId, CancellationToken ct = default);

    /// <summary>
    /// Manually triggers execution of a workflow.
    /// </summary>
    Task<WorkflowExecutionInfo> ExecuteWorkflowAsync(string workflowId, CancellationToken ct = default);

    /// <summary>
    /// Activates a workflow (enables its triggers / cron schedule).
    /// </summary>
    Task ActivateWorkflowAsync(string workflowId, CancellationToken ct = default);

    /// <summary>
    /// Deactivates a workflow (disables its triggers / cron schedule).
    /// </summary>
    Task DeactivateWorkflowAsync(string workflowId, CancellationToken ct = default);

    /// <summary>
    /// Gets the execution history for all workflows.
    /// </summary>
    Task<IReadOnlyList<WorkflowExecutionInfo>> GetExecutionsAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Gets details about a specific execution.
    /// </summary>
    Task<WorkflowExecutionInfo?> GetExecutionAsync(string executionId, CancellationToken ct = default);
}
