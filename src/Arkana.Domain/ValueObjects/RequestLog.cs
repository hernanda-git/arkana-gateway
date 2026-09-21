using Arkana.Domain.Interfaces;

namespace Arkana.Domain.ValueObjects;

/// <summary>
/// Full request/response log capturing the complete conversation,
/// tool calls, token usage, and metadata for the Logs page.
/// </summary>
public sealed record RequestLog
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string? ApiKeyName { get; init; }

    /// <summary>Tenant owning the request. Empty is retained only for legacy callers.</summary>
    public Guid TenantId { get; init; }

    /// <summary>Provider/account requested by the caller or key policy.</summary>
    public string? RequestedProviderCode { get; init; }
    public Guid? RequestedProviderAccountId { get; init; }
    public string? RequestedProviderAccountCode { get; init; }

    /// <summary>Durable identity of the account that actually served the request.</summary>
    public Guid? ResolvedProviderAccountId { get; init; }
    public string? ResolvedProviderAccountCode { get; init; }
    public string RouteKind { get; init; } = "unknown";

    /// <summary>The full conversation messages sent to the provider.</summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];

    /// <summary>The main text response from the AI.</summary>
    public string? ResponseContent { get; init; }

    /// <summary>Any tool calls made by the AI.</summary>
    public IReadOnlyList<ToolCallInfo>? ToolCalls { get; init; }

    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;
    public decimal Cost { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public bool IsError { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// When set, identifies the MITM agent that routed this request through the
    /// gateway on behalf of an employee's IDE agent (e.g. "antigravity"). Null for
    /// requests sent directly to the gateway API. Additive: never affects routing,
    /// auth, or metering — it is audit attribution only.
    /// </summary>
    public string? ViaMitmAgent { get; init; }
}
