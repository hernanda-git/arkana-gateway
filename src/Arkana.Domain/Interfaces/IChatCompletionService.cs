using System.Text.Json;
using System.Text.Json.Serialization;
using Arkana.Domain.Entities;
using Arkana.Domain.ValueObjects;
using Arkana.Domain.Interfaces.Canonical;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Unified interface for AI chat completion across providers. The request
/// shape <see cref="ChatRequest"/> is the historical/OpenAI-flavored alias;
/// the provider-neutral model lives in
/// <see cref="Arkana.Domain.Interfaces.Canonical"/>.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 14a-1): introduce the canonical request model
/// alongside the historical types. Connector implementations and the handler
/// continue to consume <see cref="ChatRequest"/> in this commit; they will
/// migrate to the canonical types in subsequent sub-tasks (14a-2, 14b-*).
/// </remarks>
public interface IChatCompletionService
{
    string ProviderName { get; }
    Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default);
}

public sealed record ChatRequest
{
    public string Model { get; init; } = string.Empty;
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
    public object? ToolChoice { get; init; }
    public string? UserId { get; init; }
    /// <summary>Authenticated tenant required by every provider connector.</summary>
    public Guid TenantId { get; init; }

    /// <summary>
    /// Optional API-key-owned ChatGPT OAuth provider pin. Null preserves the
    /// existing pool round-robin behavior.
    /// </summary>
    public string? PreferredProviderCode { get; init; }
    public Guid? PreferredProviderId { get; init; }
    public Guid? PreferredProviderAccountId { get; init; }
    public string? PreferredProviderAccountCode { get; init; }

    /// <summary>Whether an explicitly pinned key may use another provider/account.</summary>
    public bool AllowProviderFallback { get; init; }

    /// <summary>Explicit account policy for subscription connectors.</summary>
    public AccountRoutingMode AccountRoutingMode { get; init; } = AccountRoutingMode.Pool;

    /// <summary>
    /// Project this OpenAI-flavored request onto the canonical model.
    /// Used by the dialect translator (sub-task 14b-1) and by future
    /// Anthropic / Gemini connectors that consume <see cref="CanonicalChatRequest"/>
    /// directly. The reverse projection lives on <see cref="CanonicalChatRequest"/>.
    /// </summary>
    public CanonicalChatRequest ToCanonical() => new()
    {
        Model = Model,
        UserId = UserId,
        Messages = Messages.Select(m => m.ToCanonical()).ToList(),
        Tools = Tools?.Select(t => t.ToCanonical()).ToList(),
        ToolChoice = ParseToolChoice(ToolChoice),
    };

    private static CanonicalToolChoice? ParseToolChoice(object? value)
    {
        if (value is null) return CanonicalToolChoice.AutoValue;
        if (value is CanonicalToolChoice canonical) return canonical;

        JsonElement element;
        try
        {
            element = value is JsonElement jsonElement
                ? jsonElement
                : JsonSerializer.SerializeToElement(value);
        }
        catch (JsonException)
        {
            return CanonicalToolChoice.AutoValue;
        }

        if (element.ValueKind == JsonValueKind.String)
            return ParseToolChoiceName(element.GetString());
        if (element.ValueKind != JsonValueKind.Object)
            return CanonicalToolChoice.AutoValue;

        if (element.TryGetProperty("function", out var function)
            && function.ValueKind == JsonValueKind.Object
            && function.TryGetProperty("name", out var nestedName)
            && nestedName.ValueKind == JsonValueKind.String)
            return CanonicalToolChoice.ForTool(nestedName.GetString() ?? string.Empty);

        if (element.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String)
            return CanonicalToolChoice.ForTool(name.GetString() ?? string.Empty);

        if (element.TryGetProperty("mode", out var mode)
            && mode.ValueKind == JsonValueKind.String)
            return ParseToolChoiceName(mode.GetString());

        return CanonicalToolChoice.AutoValue;
    }

    private static CanonicalToolChoice ParseToolChoiceName(string? name)
        => name?.ToLowerInvariant() switch
        {
            "none" => CanonicalToolChoice.NoneValue,
            "required" or "any" => CanonicalToolChoice.AnyValue,
            _ => CanonicalToolChoice.AutoValue,
        };
}

public sealed record ChatMessage
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = string.Empty;
    public string? ToolCallId { get; init; }
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    public CanonicalMessage ToCanonical() => new()
    {
        Role = Role,
        Content = Content,
        ToolCallId = ToolCallId,
        ToolCalls = ToolCalls?.Select(tc => new CanonicalToolCall
        {
            Id = tc.Id,
            Kind = CanonicalToolCall.ToolKind.Function,
            Name = tc.Function.Name,
            Arguments = tc.Function.Arguments,
        }).ToList(),
    };
}

/// <summary>
/// Tool definition sent to the model (OpenAI-compatible format).
/// </summary>
public sealed record ToolDefinition
{
    public string Type { get; init; } = "function";
    public ToolFunction Function { get; init; } = new();

    public CanonicalToolDefinition ToCanonical() => new()
    {
        Type = Type,
        Function = Function.ToCanonical(),
    };
}

public sealed record ToolFunction
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public JsonElement? Parameters { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strict { get; init; }

    public CanonicalToolFunction ToCanonical() => new()
    {
        Name = Name,
        Description = Description,
        Parameters = Parameters,
        Strict = Strict,
    };
}

/// <summary>
/// A function call requested by the model (historical OpenAI-flavored shape;
/// canonical form is <see cref="CanonicalToolCall"/>).
/// </summary>
public sealed record ToolCall
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = "function";
    public ToolCallFunction Function { get; init; } = new();
}

public sealed record ToolCallFunction
{
    public string Name { get; init; } = string.Empty;
    public string Arguments { get; init; } = string.Empty;
}

public sealed record ChatResult
{
    public string Content { get; init; } = string.Empty;
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public string Model { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
    public Guid? ResolvedProviderAccountId { get; init; }
    public string? ResolvedProviderAccountCode { get; init; }
    public string RouteKind { get; init; } = "unknown";
    public bool IsSuccess => ErrorMessage is null;

    /// <summary>
    /// The HTTP status code the upstream provider returned, when the failure
    /// originated from an upstream call. Lets the gateway propagate a 429/401
    /// to the client instead of flattening every failure into one code.
    /// </summary>
    public int? UpstreamStatus { get; init; }

    /// <summary>True once an upstream stream has committed bytes to the caller; never retry.</summary>
    public bool StreamingCommitted { get; init; }

    /// <summary>Tool calls made by the AI in this response.</summary>
    public IReadOnlyList<ToolCallInfo>? ToolCalls { get; init; }
}
