namespace Arkana.Infrastructure.Persistence.Entities;

/// <summary>
/// EF Core entity for full request/response logs, mapped to the RequestLogs table.
/// Complex child objects (Messages, ToolCalls) are stored as JSONB columns.
/// Replaces the in-memory ConcurrentDictionary in InMemoryRequestLogger.
/// </summary>
public sealed class RequestLogEntity
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? ApiKeyName { get; set; }
    public Guid? RequestedProviderAccountId { get; set; }
    public string? RequestedProviderCode { get; set; }
    public string? RequestedProviderAccountCode { get; set; }
    public Guid? ResolvedProviderAccountId { get; set; }
    public string? ResolvedProviderAccountCode { get; set; }
    public string RouteKind { get; set; } = "unknown";

    /// <summary>When set, identifies the employee MITM agent that routed the request
    /// through the gateway (e.g. "antigravity"). Null for direct API clients.</summary>
    public string? ViaMitmAgent { get; set; }

    /// <summary>Serialized as JSONB — list of { role, content } objects.</summary>
    public string MessagesJson { get; set; } = "[]";

    /// <summary>AI response text content.</summary>
    public string? ResponseContent { get; set; }

    /// <summary>Serialized as JSONB — list of tool call objects, null if none.</summary>
    public string? ToolCallsJson { get; set; }

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal Cost { get; set; }
    public long DurationTicks { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public bool IsError { get; set; }
    public string? ErrorMessage { get; set; }

    // Multi-tenant scoping (ENT-ARKANA-001)
    public Guid TenantId { get; set; }
}
