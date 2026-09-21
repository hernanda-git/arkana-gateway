using System.Text.Json;
using System.Text.Json.Serialization;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.ValueObjects;

namespace Arkana.Infrastructure.AI.Dialects;

/// <summary>
/// Anthropic Messages API dialect translator. Projects the provider-neutral
/// <see cref="CanonicalChatRequest"/> onto Anthropic's
/// <c>/v1/messages</c> wire format and parses responses back into
/// <see cref="ChatResult"/>.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 / Phase 3 task 15a. Wire-format differences handled here:
/// <list type="bullet">
///   <item>System prompt: extracted from the first <c>role: system</c> message
///         into the top-level <c>system</c> string field (Anthropic does not
///         allow system messages in the <c>messages</c> array).</item>
///   <item>Tool results: <c>role: tool</c> messages with <c>tool_call_id</c>
///         are rewritten as user messages containing <c>tool_result</c> content
///         blocks (Anthropic's model for feeding tool outputs back).</item>
///   <item>Tool calls: <c>assistant</c> messages with <c>ToolCalls</c> are
///         serialized as <c>content</c> blocks of type <c>tool_use</c>, with
///         arguments parsed from JSON string into an <c>input</c> object.</item>
///   <item>Max tokens: Anthropic requires <c>max_tokens</c> on every request;
///         we default to 8192 when not specified. 8192 is the safe modern
///         default for Claude 3+ and matches Anthropic's own examples.</item>
///   <item>Response content: <c>content</c> is an array of typed blocks; we
///         concatenate the <c>text</c> blocks and ignore the rest.</item>
///   <item>Tool choice: maps <c>Any</c> -&gt; <c>{type: "any"}</c> and
///         <c>Specific</c> -&gt; <c>{type: "tool", name: "..."}</c>.</item>
/// </list>
/// </remarks>
public sealed class AnthropicDialectTranslator : IDialectTranslator
{
    public string DialectCode => "anthropic";

    /// <summary>
    /// Default max tokens when the caller did not specify one. Anthropic
    /// rejects requests without <c>max_tokens</c>; the canonical model
    /// has no field for it, so we always fill one in.
    /// </summary>
    public const int DefaultMaxTokens = 8192;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Dictionary<string, object?> ToRequestBody(CanonicalChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Extract system prompt (if any) and reformat messages for Anthropic.
        string? systemPrompt = null;
        var anthropicMessages = new List<Dictionary<string, object?>>();

        foreach (var msg in request.Messages)
        {
            if (msg.Role == "system")
            {
                // Concatenate multiple system messages with double newline
                // (Anthropic's recommended pattern).
                systemPrompt = systemPrompt is null
                    ? msg.Content
                    : systemPrompt + "\n\n" + msg.Content;
                continue;
            }

            anthropicMessages.Add(SerializeMessage(msg));
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrEmpty(request.Model) ? "claude-3-5-sonnet-latest" : request.Model,
            ["messages"] = anthropicMessages,
            ["max_tokens"] = DefaultMaxTokens,
        };

        if (systemPrompt is not null)
        {
            body["system"] = systemPrompt;
        }

        if (request.Tools is { Count: > 0 })
        {
            body["tools"] = request.Tools.Select(SerializeTool).ToList();
        }

        if (request.ToolChoice is not null)
        {
            body["tool_choice"] = SerializeToolChoice(request.ToolChoice);
        }

        return body;
    }

    public ChatResult FromResponseBody(string responseJson, string requestedModel, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(responseJson);

        AnthropicResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AnthropicResponse>(responseJson, JsonOpts);
        }
        catch (JsonException)
                {
                    return new ChatResult
            {
                ErrorMessage = "Anthropic dialect: malformed JSON response.",
                Duration = duration,
                Model = requestedModel,
            };
        }

        if (parsed is null)
        {
            return new ChatResult
            {
                ErrorMessage = "Anthropic dialect: empty response from upstream",
                Duration = duration,
                Model = requestedModel,
            };
        }

        // Concatenate text blocks; collect tool_use blocks.
        var textParts = new List<string>();
        var toolCalls = new List<ToolCallInfo>();

        if (parsed.content is { Length: > 0 })
        {
            foreach (var block in parsed.content)
            {
                if (block is null) continue;

                if (string.Equals(block.type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(block.text))
                    {
                        textParts.Add(block.text);
                    }
                }
                else if (string.Equals(block.type, "tool_use", StringComparison.OrdinalIgnoreCase))
                {
                    if (block.id is not null && block.name is not null)
                    {
                        // Anthropic returns input as a JSON object; we
                        // re-serialize to a string to match ChatToolCallInfo's
                        // FunctionArguments contract (a string the caller
                        // parses).
                        var argumentsJson = block.input is not null
                            ? block.input.Value.GetRawText()
                            : "{}";

                        toolCalls.Add(new ToolCallInfo
                        {
                            Id = block.id,
                            Type = "function",
                            FunctionName = block.name,
                            FunctionArguments = argumentsJson,
                        });
                    }
                }
            }
        }

        return new ChatResult
        {
            Content = string.Concat(textParts),
            InputTokens = parsed.usage?.input_tokens ?? 0,
            OutputTokens = parsed.usage?.output_tokens ?? 0,
            Model = parsed.model ?? requestedModel,
            Duration = duration,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
        };
    }

    // ── request-side helpers ──────────────────────────────────────

    private static Dictionary<string, object?> SerializeMessage(CanonicalMessage msg)
    {
        // Tool-result messages: rewrite as user message with tool_result content blocks.
        if (msg.Role == "tool" && !string.IsNullOrEmpty(msg.ToolCallId))
        {
            return new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = msg.ToolCallId,
                        ["content"] = msg.Content,
                    }
                },
            };
        }

        // Assistant messages with tool calls: each tool call becomes a
        // tool_use content block alongside the text content (if any).
        if (msg.Role == "assistant" && msg.ToolCalls is { Count: > 0 })
        {
            var contentBlocks = new List<Dictionary<string, object?>>();

            if (!string.IsNullOrEmpty(msg.Content))
            {
                contentBlocks.Add(new()
                {
                    ["type"] = "text",
                    ["text"] = msg.Content,
                });
            }

            foreach (var call in msg.ToolCalls)
            {
                contentBlocks.Add(SerializeToolUseBlock(call));
            }

            return new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = contentBlocks,
            };
        }

        // Plain user/assistant text message.
        return new Dictionary<string, object?>
        {
            ["role"] = msg.Role,
            ["content"] = msg.Content,
        };
    }

    private static Dictionary<string, object?> SerializeToolUseBlock(CanonicalToolCall call)
    {
        // Anthropic expects the arguments as a JSON object, not a string.
        // Try to parse the arguments as JSON; fall back to wrapping the
        // raw string in a {"raw": "..."} envelope so the request still
        // validates as a JSON object.
        JsonElement input;
        if (!string.IsNullOrEmpty(call.Arguments))
        {
            try
            {
                input = JsonDocument.Parse(call.Arguments).RootElement.Clone();
            }
            catch (JsonException)
            {
                input = JsonDocument.Parse("""{"raw":""}""").RootElement.Clone();
                input = JsonDocument.Parse($"{{\"raw\":{JsonSerializer.Serialize(call.Arguments)}}}").RootElement.Clone();
            }
        }
        else
        {
            input = JsonDocument.Parse("{}").RootElement.Clone();
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "tool_use",
            ["id"] = call.Id,
            ["name"] = call.Name,
            ["input"] = input,
        };
    }

    private static Dictionary<string, object?> SerializeTool(CanonicalToolDefinition tool) => new()
    {
        // Anthropic has no "type" wrapper on tools; the function fields
        // are top-level. Schema is the same JSON Schema shape as OpenAI.
        ["name"] = tool.Function.Name,
        ["description"] = tool.Function.Description,
        ["input_schema"] = tool.Function.Parameters,
    };

    private static Dictionary<string, object?> SerializeToolChoice(CanonicalToolChoice choice) => choice.ChoiceMode switch
    {
        CanonicalToolChoice.Mode.Auto => new() { ["type"] = "auto" },
        CanonicalToolChoice.Mode.Any => new() { ["type"] = "any" },
        CanonicalToolChoice.Mode.None => new() { ["type"] = "none" },
        CanonicalToolChoice.Mode.Specific when choice.ToolName is { } name =>
            new() { ["type"] = "tool", ["name"] = name },
        _ => new() { ["type"] = "auto" },
    };

    // ── response-side wire format (private to the translator) ──────

    private sealed record AnthropicResponse(
        string? id,
        string? type,
        string? model,
        string? role,
        AnthropicContentBlock[]? content,
        string? stop_reason,
        AnthropicUsage? usage);

    private sealed record AnthropicContentBlock(
        string? type,
        string? text,
        string? id,
        string? name,
        JsonElement? input);

    private sealed record AnthropicUsage(int input_tokens, int output_tokens);
}
