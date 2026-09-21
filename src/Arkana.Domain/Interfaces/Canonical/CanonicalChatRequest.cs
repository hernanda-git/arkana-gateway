using System.Text.Json;
using Arkana.Domain.ValueObjects;

namespace Arkana.Domain.Interfaces.Canonical;

/// <summary>
/// Provider-neutral chat completion request. The canonical surface that all
/// <see cref="IChatCompletionService"/> implementations accept. Dialect
/// translators (OpenAI, Anthropic, Gemini) project this into their wire format.
/// </summary>
/// <remarks>
/// Field shape is intentionally minimal: it carries what every provider needs
/// and nothing that is provider-specific. Provider-specific options belong on
/// a dialect request wrapper, not on the canonical model.
/// </remarks>
public sealed record CanonicalChatRequest
{
    /// <summary>
    /// Target model identifier in the gateway's own naming scheme
    /// (matches <c>Model.Code</c> in the provider catalog).
    /// </summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// Ordered message history. Order is significant — the first message
    /// is conventionally the system prompt when one is present.
    /// </summary>
    public IReadOnlyList<CanonicalMessage> Messages { get; init; } = [];

    /// <summary>
    /// Tools/functions the model may invoke. Null means no tool use.
    /// </summary>
    public IReadOnlyList<CanonicalToolDefinition>? Tools { get; init; }

    /// <summary>
    /// Tool choice directive. See <see cref="CanonicalToolChoice"/> for
    /// the normalized shape; dialects translate to their native form.
    /// </summary>
    public CanonicalToolChoice? ToolChoice { get; init; }

    /// <summary>
    /// Optional caller identity, propagated to upstreams that support it
    /// (OpenAI <c>user</c> field, Anthropic metadata, etc.) for abuse detection.
    /// </summary>
    public string? UserId { get; init; }
}

/// <summary>
/// Tool choice directive, normalized. Translators map to the dialect's shape.
/// </summary>
public sealed record CanonicalToolChoice
{
    public enum Mode { Auto, Any, None, Specific }

    public Mode ChoiceMode { get; init; } = Mode.Auto;
    public string? ToolName { get; init; }

    public static readonly CanonicalToolChoice AutoValue = new() { ChoiceMode = Mode.Auto };
    public static readonly CanonicalToolChoice AnyValue = new() { ChoiceMode = Mode.Any };
    public static readonly CanonicalToolChoice NoneValue = new() { ChoiceMode = Mode.None };

    public static CanonicalToolChoice ForTool(string name) =>
        new() { ChoiceMode = Mode.Specific, ToolName = name };
}

/// <summary>
/// Provider-neutral message in a conversation. The <see cref="Role"/> follows
/// the OpenAI taxonomy (<c>system</c> / <c>user</c> / <c>assistant</c> /
/// <c>tool</c>) because every supported provider can express that taxonomy
/// with a simple projection.
/// </summary>
public sealed record CanonicalMessage
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = string.Empty;

    /// <summary>For <c>tool</c> role messages — the ID of the tool call this is responding to.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>For <c>assistant</c> role messages — tool calls the model wants to make.</summary>
    public IReadOnlyList<CanonicalToolCall>? ToolCalls { get; init; }
}

/// <summary>
/// A single tool/function the model is being told it may call.
/// Provider-neutral; dialect translators serialize to the wire format.
/// </summary>
public sealed record CanonicalToolDefinition
{
    /// <summary>
    /// Tool kind. Today always <c>function</c>; reserved for future
    /// tool types (e.g. code-interpreter, web-search) that some providers
    /// expose as first-class tool kinds.
    /// </summary>
    public string Type { get; init; } = "function";

    public CanonicalToolFunction Function { get; init; } = new();
}

public sealed record CanonicalToolFunction
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }

    /// <summary>
    /// JSON Schema for the function's parameters, kept as <see cref="JsonElement"/>
    /// so dialects can serialize it without round-tripping through .NET types.
    /// </summary>
    public JsonElement? Parameters { get; init; }

    /// <summary>OpenAI <c>strict</c> mode. Other dialects ignore this flag.</summary>
    public bool? Strict { get; init; }
}

/// <summary>
/// A tool/function call the model wants to make. <see cref="Kind"/> is a
/// discriminated union (function today; code-interpreter / web-search
/// reserved for future providers) so new tool types can be added without
/// breaking the function-call case.
/// </summary>
public sealed record CanonicalToolCall
{
    public enum ToolKind { Function, CodeInterpreter, WebSearch }

    public string Id { get; init; } = string.Empty;
    public ToolKind Kind { get; init; } = ToolKind.Function;
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Serialized arguments (a JSON object string). Stored as a string
    /// so dialect translators that need to re-parse it don't have to
    /// re-serialize first.
    /// </summary>
    public string Arguments { get; init; } = string.Empty;
}

/// <summary>
/// Normalized token usage returned by a provider. Every provider reports
/// input and output counts under different names; the canonical model
/// collapses them so callers (metering, rate limiter, cache key) see
/// one shape.
/// </summary>
public sealed record CanonicalUsage
{
    /// <summary>Prompt / input tokens consumed.</summary>
    public int InputTokens { get; init; }

    /// <summary>Completion / output tokens generated.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Total of input + output. Optional — some providers don't report it.</summary>
    public int? TotalTokens { get; init; }

    public int Total => TotalTokens ?? (InputTokens + OutputTokens);
}
