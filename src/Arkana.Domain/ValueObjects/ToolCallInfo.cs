namespace Arkana.Domain.ValueObjects;

/// <summary>
/// Represents a single tool/function call from an AI response.
/// </summary>
public sealed record ToolCallInfo
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = "function";
    public string FunctionName { get; init; } = string.Empty;
    public string FunctionArguments { get; init; } = string.Empty;
}
