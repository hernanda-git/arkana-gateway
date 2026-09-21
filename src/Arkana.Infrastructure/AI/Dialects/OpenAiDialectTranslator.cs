using System.Text.Json;
using System.Text.Json.Serialization;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.ValueObjects;

namespace Arkana.Infrastructure.AI.Dialects;

/// <summary>
/// OpenAI-compatible dialect translator. Handles the request/response wire
/// format used by OpenAI, OpenCode (opencode.ai/zen/go/v1), DeepSeek
/// (api.deepseek.com), and CLIProxyAPI. They all share the OpenAI
/// <c>/v1/chat/completions</c> shape so a single translator serves all four
/// — provider-specific differences (base URL, auth header) live in the
/// connector, not here.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 14b-1).
/// </remarks>
public sealed class OpenAiDialectTranslator : IDialectTranslator
{
    /// <summary>
    /// <c>"openai"</c> by default, but the factory matches by <c>AiProvider.Code</c>,
    /// so a provider row with code <c>"opencode"</c>, <c>"deepseek"</c>, or
    /// <c>"cliproxyapi"</c> will still pick this translator up. The
    /// <see cref="DialectCode"/> property is the canonical name for telemetry
    /// and metrics labels.
    /// </summary>
    public string DialectCode => "openai";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Dictionary<string, object?> ToRequestBody(CanonicalChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrEmpty(request.Model) ? "gpt-4o-mini" : request.Model,
            ["messages"] = request.Messages.Select(SerializeMessage).ToList(),
            ["stream"] = false,
        };

        if (request.UserId is not null)
        {
            body["user"] = request.UserId;
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

        OpenAiResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<OpenAiResponse>(responseJson, JsonOpts);
        }
        catch (JsonException)
        {
            return new ChatResult
            {
                ErrorMessage = "OpenAI dialect: invalid JSON response.",
                Duration = duration,
                Model = requestedModel,
            };
        }

        if (parsed is null)
        {
            return new ChatResult
            {
                ErrorMessage = "OpenAI dialect: empty response from upstream",
                Duration = duration,
                Model = requestedModel,
            };
        }

        var firstChoice = parsed.choices?.FirstOrDefault();
        var message = firstChoice?.message;

        var toolCalls = ParseToolCalls(message?.tool_calls);

        return new ChatResult
        {
            Content = message?.content ?? string.Empty,
            InputTokens = parsed.usage?.prompt_tokens ?? 0,
            OutputTokens = parsed.usage?.completion_tokens ?? 0,
            Model = parsed.model ?? requestedModel,
            Duration = duration,
            ToolCalls = toolCalls,
        };
    }

    // ── request-side helpers ─────────────────────────────────────────

    private static Dictionary<string, object?> SerializeMessage(CanonicalMessage msg)
    {
        var dict = new Dictionary<string, object?>
        {
            ["role"] = msg.Role,
            ["content"] = string.IsNullOrEmpty(msg.Content) ? null : msg.Content,
        };

        if (msg.Role == "tool" && !string.IsNullOrEmpty(msg.ToolCallId))
        {
            dict["tool_call_id"] = msg.ToolCallId;
        }

        if (msg.Role == "assistant" && msg.ToolCalls is { Count: > 0 })
        {
            dict["tool_calls"] = msg.ToolCalls.Select(SerializeToolCall).ToList();
        }

        return dict;
    }

    private static Dictionary<string, object?> SerializeTool(CanonicalToolDefinition tool) => new()
    {
        ["type"] = tool.Type,
        ["function"] = new Dictionary<string, object?>
        {
            ["name"] = tool.Function.Name,
            ["description"] = tool.Function.Description,
            ["parameters"] = tool.Function.Parameters,
            ["strict"] = tool.Function.Strict,
        },
    };

    private static Dictionary<string, object?> SerializeToolCall(CanonicalToolCall call) => new()
    {
        ["id"] = call.Id,
        ["type"] = "function", // OpenAI-compat only emits function-style tool calls today
        ["function"] = new Dictionary<string, object?>
        {
            ["name"] = call.Name,
            ["arguments"] = call.Arguments,
        },
    };

    private static object SerializeToolChoice(CanonicalToolChoice choice) => choice.ChoiceMode switch
    {
        CanonicalToolChoice.Mode.Auto => "auto",
        CanonicalToolChoice.Mode.Any => "required",
        CanonicalToolChoice.Mode.None => "none",
        CanonicalToolChoice.Mode.Specific when choice.ToolName is { } name =>
            new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?> { ["name"] = name },
            },
        _ => "auto",
    };

    // ── response-side helpers ────────────────────────────────────────

    private static List<ToolCallInfo>? ParseToolCalls(OpenAiToolCall[]? raw)
    {
        if (raw is not { Length: > 0 })
        {
            return null;
        }

        return raw.Select(tc => new ToolCallInfo
        {
            Id = tc.id ?? string.Empty,
            Type = tc.type ?? "function",
            FunctionName = tc.function?.name ?? string.Empty,
            FunctionArguments = tc.function?.arguments ?? string.Empty,
        }).ToList();
    }

    // ── wire format (private to the translator) ──────────────────────

    private sealed record OpenAiResponse(
        string? id,
        string? model,
        OpenAiChoice[]? choices,
        OpenAiUsage? usage);

    private sealed record OpenAiChoice(int index, OpenAiMessage message);

    private sealed record OpenAiMessage(string role, string? content, OpenAiToolCall[]? tool_calls);

    private sealed record OpenAiToolCall(string? id, string? type, OpenAiToolCallFunction? function);

    private sealed record OpenAiToolCallFunction(string? name, string? arguments);

    private sealed record OpenAiUsage(int prompt_tokens, int completion_tokens, int total_tokens);
}
