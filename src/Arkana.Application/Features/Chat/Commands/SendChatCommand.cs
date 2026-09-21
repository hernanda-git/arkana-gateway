using MediatR;
using System.Text.Json;
using Arkana.Domain.ValueObjects;

namespace Arkana.Application.Features.Chat.Commands;

/// <summary>
/// Send a chat completion request through the AI Gateway.
/// </summary>
public sealed record SendChatCommand : IRequest<SendChatResult>
{
    public string Model { get; init; } = string.Empty;
    public IReadOnlyList<ChatMessageDto> Messages { get; init; } = [];
    public IReadOnlyList<ToolDefinitionDto>? Tools { get; init; }
    public JsonElement? ToolChoice { get; init; }
    public string? PreferredProvider { get; init; }
    public bool AllowProviderFallback { get; init; }
    public string? ApiKey { get; init; }

    /// <summary>
    /// Additive audit attribution: identifies the employee MITM agent (e.g. "antigravity")
    /// that routed this request. Null for requests sent directly to the gateway API.
    /// Never affects routing, auth, or metering.
    /// </summary>
    public string? ViaMitmAgent { get; init; }
}

/// <summary>
/// A single chat message with role and content.
/// </summary>
public sealed record ChatMessageDto
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = string.Empty;

    /// <summary>For "tool" role messages — the ID of the tool call this is responding to.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    /// <summary>For "assistant" role messages — tool calls the model wants to make.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("tool_calls")]
    public IReadOnlyList<ToolCallDto>? ToolCalls { get; init; }
}

/// <summary>
/// Tool definition in OpenAI-compatible format.
/// </summary>
public sealed record ToolDefinitionDto
{
    public string Type { get; init; } = "function";
    public ToolFunctionDto Function { get; init; } = new();
}

public sealed record ToolFunctionDto
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public JsonElement? Parameters { get; init; }
    public bool? Strict { get; init; }
}

/// <summary>
/// A function call requested by the model.
/// </summary>
public sealed record ToolCallDto
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = "function";
    public ToolCallFunctionDto Function { get; init; } = new();
}

public sealed record ToolCallFunctionDto
{
    public string Name { get; init; } = string.Empty;
    public string Arguments { get; init; } = string.Empty;
}

/// <summary>
/// Result of a chat completion request.
/// </summary>
public sealed record SendChatResult
{
    public string Content { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;
    public decimal EstimatedCost { get; init; }
    public long DurationMs { get; init; }
    public IReadOnlyList<ToolCallInfo>? ToolCalls { get; init; }

    /// <summary>
    /// True when the request failed. Callers MUST check this rather than
    /// sniffing <see cref="Content"/> for an "Error: " prefix — a failed
    /// completion previously surfaced as HTTP 200 with the error text in the
    /// assistant message, which is indistinguishable from a real answer.
    /// </summary>
    public bool IsError { get; init; }

    /// <summary>Machine-readable error type, e.g. "upstream_error", "auth_error".</summary>
    public string? ErrorType { get; init; }

    /// <summary>
    /// HTTP status the gateway should return. Null for success. Upstream
    /// status codes are mapped by <c>UpstreamStatus</c> so that a 429 from a
    /// provider surfaces to the client as a 429, not a 200 or a flat 500.
    /// </summary>
    public int? StatusCode { get; init; }
}
