using System.Text.Json;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.ValueObjects;

namespace Arkana.Infrastructure.AI.Dialects;

/// <summary>
/// Google Gemini API dialect translator. Projects the provider-neutral
/// <see cref="CanonicalChatRequest"/> onto Gemini's
/// <c>generateContent</c> wire format and parses responses back into
/// <see cref="ChatResult"/>.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 / Phase 3 task 15d. Wire-format differences handled here:
/// <list type="bullet">
///   <item>System prompt: extracted from the first <c>role: system</c> message
///         into the top-level <c>systemInstruction</c> object (Gemini does not
///         allow system messages in the <c>contents</c> array).</item>
///   <item>Roles: Gemini uses <c>user</c> / <c>model</c> instead of
///         <c>user</c> / <c>assistant</c>.</item>
///   <item>Tool results: <c>role: tool</c> messages with <c>tool_call_id</c>
///         are rewritten as user messages containing <c>functionResponse</c>
///         parts.</item>
///   <item>Tool calls: <c>assistant</c> messages with <c>ToolCalls</c> are
///         serialized as <c>functionCall</c> parts, with arguments as a JSON
///         object.</item>
///   <item>Tools: Gemini wraps functions in <c>functionDeclarations</c> under
///         the <c>tools</c> array (each tool is a separate <c>tools[{functionDeclarations}]</c>
///         entry).</item>
///   <item>Tool choice: maps to <c>toolConfig.functionCallingConfig.mode</c>
///         with values <c>AUTO</c>, <c>ANY</c>, <c>NONE</c>.</item>
///   <item>Response content: <c>candidates[0].content.parts</c> array. Text
///         parts are concatenated; <c>functionCall</c> parts become tool calls.</item>
///   <item>Token counts: read from <c>usageMetadata.promptTokenCount</c> /
///         <c>candidatesTokenCount</c>.</item>
/// </list>
/// </remarks>
public sealed class GeminiDialectTranslator : IDialectTranslator
{
    public string DialectCode => "gemini";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public Dictionary<string, object?> ToRequestBody(CanonicalChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Extract system prompt (if any) and reformat messages for Gemini.
        string? systemPrompt = null;
        var geminiContents = new List<Dictionary<string, object?>>();

        foreach (var msg in request.Messages)
        {
            if (msg.Role == "system")
            {
                // Concatenate multiple system messages.
                systemPrompt = systemPrompt is null
                    ? msg.Content
                    : systemPrompt + "\n\n" + msg.Content;
                continue;
            }

            geminiContents.Add(SerializeMessage(msg));
        }

        var body = new Dictionary<string, object?>
        {
            ["contents"] = geminiContents,
        };

        if (systemPrompt is not null)
        {
            body["systemInstruction"] = new Dictionary<string, object?>
            {
                ["parts"] = new[] { new Dictionary<string, object?> { ["text"] = systemPrompt } },
            };
        }

        if (request.Tools is { Count: > 0 })
        {
            // Gemini requires each tool as a separate {functionDeclarations: [...]} entry.
            body["tools"] = request.Tools.Select(SerializeTool).ToList();
        }

        if (request.ToolChoice is not null)
        {
            body["toolConfig"] = SerializeToolChoice(request.ToolChoice);
        }

        return body;
    }

    public ChatResult FromResponseBody(string responseJson, string requestedModel, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(responseJson);

        GeminiResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GeminiResponse>(responseJson, JsonOpts);
        }
        catch (JsonException)
        {
            return new ChatResult
            {
                ErrorMessage = "Gemini: invalid JSON response.",
                Duration = duration,
                Model = requestedModel,
            };
        }

        if (parsed is null)
        {
            return new ChatResult
            {
                ErrorMessage = "Gemini dialect: empty response from upstream",
                Duration = duration,
                Model = requestedModel,
            };
        }

        // Check for blocked content via finishReason or promptFeedback.
        if (parsed.promptFeedback?.blockReason is not null)
        {
            return new ChatResult
            {
                ErrorMessage = "Gemini: content blocked by provider safety policy.",
                Duration = duration,
                Model = requestedModel,
            };
        }

        var candidate = parsed.candidates?.FirstOrDefault();
        if (candidate is null)
        {
            return new ChatResult
            {
                ErrorMessage = "Gemini: no candidates in response",
                Duration = duration,
                Model = requestedModel,
            };
        }

        if (candidate.finishReason is null)
        {
            return new ChatResult
            {
                ErrorMessage = "Gemini: response blocked by provider policy.",
                Duration = duration,
                Model = requestedModel,
            };
        }

        if (candidate.finishReason is not "STOP" and not "MAX_TOKENS")
        {
            return new ChatResult
            {
                ErrorMessage = candidate.finishReason == "SAFETY"
                    ? "Gemini: response blocked by safety policy."
                    : "Gemini: response blocked by provider policy.",
                Duration = duration,
                Model = requestedModel,
            };
        }

        if (candidate.safetyRatings?.Any(r => r.blocked == true) == true)
        {
            return new ChatResult
            {
                ErrorMessage = "Gemini: response blocked by safety policy.",
                Duration = duration,
                Model = requestedModel,
            };
        }

        // Extract text and function calls from parts.
        var textParts = new List<string>();
        var toolCalls = new List<ToolCallInfo>();

        if (candidate.content?.parts is { Length: > 0 } parts)
        {
            foreach (var part in parts)
            {
                if (!string.IsNullOrEmpty(part.text))
                {
                    textParts.Add(part.text);
                }

                if (part.functionCall is not null)
                {
                    var argsJson = part.functionCall.args is not null
                        ? part.functionCall.args.Value.GetRawText()
                        : "{}";

                    toolCalls.Add(new ToolCallInfo
                    {
                        Id = $"gemini:{part.functionCall.name ?? "unknown"}:{toolCalls.Count}",
                        Type = "function",
                        FunctionName = part.functionCall.name ?? "unknown",
                        FunctionArguments = argsJson,
                    });
                }
            }
        }

        return new ChatResult
        {
            Content = string.Concat(textParts),
            InputTokens = parsed.usageMetadata?.promptTokenCount ?? 0,
            OutputTokens = parsed.usageMetadata?.candidatesTokenCount ?? 0,
            Model = requestedModel,
            Duration = duration,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
        };
    }

    // ── request-side helpers ──────────────────────────────────────

    private static Dictionary<string, object?> SerializeMessage(CanonicalMessage msg)
    {
        // Map assistant -> model role for Gemini.
        var role = msg.Role switch
        {
            "assistant" => "model",
            _ => msg.Role,
        };

        // Tool-result messages: rewrite as user message with functionResponse part.
        if (msg.Role == "tool" && !string.IsNullOrEmpty(msg.ToolCallId))
        {
            var functionName = FunctionNameFromToolCallId(msg.ToolCallId);
            return new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["parts"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["functionResponse"] = new Dictionary<string, object?>
                        {
                            ["name"] = functionName,
                            ["response"] = new Dictionary<string, object?>
                            {
                                ["name"] = functionName,
                                ["content"] = msg.Content,
                            },
                        },
                    },
                },
            };
        }

        // Model messages with tool calls: each tool call becomes a functionCall part.
        if (role == "model" && msg.ToolCalls is { Count: > 0 })
        {
            var parts = new List<Dictionary<string, object?>>();

            if (!string.IsNullOrEmpty(msg.Content))
            {
                parts.Add(new() { ["text"] = msg.Content });
            }

            foreach (var call in msg.ToolCalls)
            {
                JsonElement args;
                if (!string.IsNullOrEmpty(call.Arguments))
                {
                    try { args = JsonDocument.Parse(call.Arguments).RootElement.Clone(); }
                    catch { args = JsonDocument.Parse("{}").RootElement.Clone(); }
                }
                else
                {
                    args = JsonDocument.Parse("{}").RootElement.Clone();
                }

                parts.Add(new()
                {
                    ["functionCall"] = new Dictionary<string, object?>
                    {
                        ["name"] = call.Name,
                        ["args"] = args,
                    },
                });
            }

            return new Dictionary<string, object?>
            {
                ["role"] = "model",
                ["parts"] = parts,
            };
        }

        // Plain text message.
        return new Dictionary<string, object?>
        {
            ["role"] = role,
            ["parts"] = new[]
            {
                new Dictionary<string, object?> { ["text"] = msg.Content },
            },
        };
    }

    private static Dictionary<string, object?> SerializeTool(CanonicalToolDefinition tool) => new()
    {
        ["functionDeclarations"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = tool.Function.Name,
                ["description"] = tool.Function.Description,
                ["parameters"] = tool.Function.Parameters,
            },
        },
    };

    private static Dictionary<string, object?> SerializeToolChoice(CanonicalToolChoice choice)
    {
        var mode = choice.ChoiceMode switch
        {
            CanonicalToolChoice.Mode.Auto => "AUTO",
            CanonicalToolChoice.Mode.Any => "ANY",
            CanonicalToolChoice.Mode.None => "NONE",
            CanonicalToolChoice.Mode.Specific => "ANY", // Gemini doesn't support named tool forcing
            _ => "AUTO",
        };

        var config = new Dictionary<string, object?>
        {
            ["functionCallingConfig"] = new Dictionary<string, object?>
            {
                ["mode"] = mode,
            },
        };

        if (choice.ChoiceMode == CanonicalToolChoice.Mode.Specific
            && !string.IsNullOrWhiteSpace(choice.ToolName)
            && config["functionCallingConfig"] is Dictionary<string, object?> functionConfig)
        {
            functionConfig["allowedFunctionNames"] = new[] { choice.ToolName };
        }

        return config;
    }

    private static string FunctionNameFromToolCallId(string id)
    {
        if (id.StartsWith("gemini:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = id.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]))
                return parts[1];
        }

        var separator = id.IndexOf(':');
        return separator > 0 ? id[..separator] : id;
    }

    // ── response-side wire format ─────────────────────────────────

    private sealed record GeminiResponse(
        GeminiCandidate[]? candidates,
        GeminiPromptFeedback? promptFeedback,
        GeminiUsageMetadata? usageMetadata);

    private sealed record GeminiCandidate(
        GeminiContent? content,
        string? finishReason,
        GeminiSafetyRating[]? safetyRatings);

    private sealed record GeminiContent(
        string? role,
        GeminiPart[]? parts);

    private sealed record GeminiPart(
        string? text,
        GeminiFunctionCall? functionCall);

    private sealed record GeminiFunctionCall(
        string? name,
        JsonElement? args);

    private sealed record GeminiSafetyRating(
        string? category,
        string? probability,
        bool? blocked);

    private sealed record GeminiPromptFeedback(
        string? blockReason);

    private sealed record GeminiUsageMetadata(
        int promptTokenCount,
        int candidatesTokenCount,
        int? totalTokenCount);
}
