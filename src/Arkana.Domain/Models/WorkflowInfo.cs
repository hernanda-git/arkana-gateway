namespace Arkana.Domain.Models;

/// <summary>
/// Represents a workflow as returned by the n8n REST API.
/// </summary>
public sealed record WorkflowInfo(
    string Id,
    string Name,
    bool Active,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    WorkflowTriggerType TriggerType,
    string? CronExpression,
    string? WebhookUrl,
    int NodeCount,
    IEnumerable<string> Tags);

/// <summary>
/// Describes how a workflow can be triggered.
/// </summary>
public enum WorkflowTriggerType
{
    /// <summary>Workflow has no automatic triggers — manual only.</summary>
    Manual,
    /// <summary>Workflow has a cron/schedule trigger node.</summary>
    Cron,
    /// <summary>Workflow has a webhook trigger node.</summary>
    Webhook,
    /// <summary>Workflow has multiple trigger types.</summary>
    Mixed
}

/// <summary>
/// Represents a workflow execution as returned by the n8n REST API.
/// </summary>
public sealed record WorkflowExecutionInfo(
    string Id,
    string WorkflowId,
    string WorkflowName,
    string Status,          // "success", "error", "running", "waiting", "crashed"
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Mode,           // "trigger", "webhook", "manual", "cli", "retry"
    string? ErrorMessage);
