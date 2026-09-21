using System.Text.Encodings.Web;
using System.Text.Json;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.AI.Dialects;
using FluentAssertions;
using Xunit;

namespace Arkana.Infrastructure.Tests.AI.Dialects;

/// <summary>
/// Pins the wire format produced by <see cref="GeminiDialectTranslator"/>.
/// These tests are the dialect's contract: the connector and any
/// future work that touches the Gemini path rely on these shapes.
/// </summary>
public class GeminiDialectTranslatorTests
{
    private readonly GeminiDialectTranslator _sut = new();

    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Serialize(Dictionary<string, object?> body) =>
        JsonSerializer.Serialize(body, ReadableJson);

    // ── request side ──────────────────────────────────────────────

    [Fact]
    public void ToRequestBody_uses_generateContent_shape()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gemini-2.0-flash",
            Messages = [new() { Role = "user", Content = "hi" }],
        };

        var body = _sut.ToRequestBody(req);

        body.Should().ContainKey("contents");
        // Model goes in URL, not body
        body.Should().NotContainKey("model");
    }

    [Fact]
    public void ToRequestBody_uses_model_role_instead_of_assistant()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gemini-2.0-flash",
            Messages =
            [
                new() { Role = "user", Content = "hello" },
                new() { Role = "assistant", Content = "world" },
            ],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        // The assistant message must be serialized as role "model"
        json.Should().Contain("\"model\"");
        json.Should().NotContain("\"assistant\"");
    }

    [Fact]
    public void ToRequestBody_extracts_system_instruction()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gemini-2.0-flash",
            Messages =
            [
                new() { Role = "system", Content = "be brief" },
                new() { Role = "user", Content = "hi" },
            ],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        // System goes to systemInstruction.parts[0].text
        json.Should().Contain("\"systemInstruction\"");
        json.Should().Contain("\"be brief\"");
        json.Should().NotContain("\"role\":\"system\"");
    }

    [Fact]
    public void ToRequestBody_concatenates_multiple_system_messages()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gemini-2.0-flash",
            Messages =
            [
                new() { Role = "system", Content = "be brief" },
                new() { Role = "system", Content = "be polite" },
                new() { Role = "user", Content = "hi" },
            ],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"be brief\\n\\nbe polite\"");
    }

    [Fact]
    public void ToRequestBody_omits_system_instruction_when_no_system_message()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gemini-2.0-flash",
            Messages = [new() { Role = "user", Content = "hi" }],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().NotContain("systemInstruction");
    }

    [Fact]
    public void ToRequestBody_serializes_tools_as_functionDeclarations()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gemini-2.0-flash",
            Messages = [new() { Role = "user", Content = "what's the weather?" }],
            Tools =
            [
                new()
                {
                    Type = "function",
                    Function = new CanonicalToolFunction
                    {
                        Name = "get_weather",
                        Description = "Get current weather",
                        Parameters = JsonDocument.Parse("""{"type":"object","properties":{"loc":{"type":"string"}}}""").RootElement,
                    },
                },
            ],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        // Gemini wraps function definitions in functionDeclarations
        json.Should().Contain("functionDeclarations");
        json.Should().Contain("get_weather");
    }

    [Fact]
    public void ToRequestBody_serializes_tool_choice_as_toolConfig()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gemini-2.0-flash",
            Messages = [new() { Role = "user", Content = "pick a tool" }],
            ToolChoice = CanonicalToolChoice.AnyValue,
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("toolConfig");
        json.Should().Contain("functionCallingConfig");
        json.Should().Contain("ANY");
    }

    // ── response side ─────────────────────────────────────────────

    [Fact]
    public void FromResponseBody_parses_text_response()
    {
        var json = """
        {
            "candidates": [{
                "content": {
                    "role": "model",
                    "parts": [{"text": "Hello there!"}]
                },
                "finishReason": "STOP"
            }],
            "usageMetadata": {
                "promptTokenCount": 10,
                "candidatesTokenCount": 5,
                "totalTokenCount": 15
            }
        }
        """;

        var result = _sut.FromResponseBody(json, "gemini-2.0-flash", TimeSpan.FromMilliseconds(100));

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Be("Hello there!");
        result.InputTokens.Should().Be(10);
        result.OutputTokens.Should().Be(5);
        result.Model.Should().Be("gemini-2.0-flash");
    }

    [Fact]
    public void FromResponseBody_parses_empty_response()
    {
        var json = """{"candidates":[{"content":{"role":"model","parts":[]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":0,"candidatesTokenCount":0}}""";

        var result = _sut.FromResponseBody(json, "gemini-2.0-flash", TimeSpan.Zero);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().BeEmpty();
    }

    [Fact]
    public void FromResponseBody_parses_function_call()
    {
        var json = """
        {
            "candidates": [{
                "content": {
                    "role": "model",
                    "parts": [
                        {"text": "Let me check the weather"},
                        {"functionCall": {"name": "get_weather", "args": {"loc": "Jakarta"}}}
                    ]
                },
                "finishReason": "STOP"
            }],
            "usageMetadata": {"promptTokenCount": 20, "candidatesTokenCount": 15}
        }
        """;

        var result = _sut.FromResponseBody(json, "gemini-2.0-flash", TimeSpan.FromMilliseconds(200));

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Be("Let me check the weather");
        result.ToolCalls.Should().NotBeNull();
        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls![0].FunctionName.Should().Be("get_weather");
        result.ToolCalls[0].FunctionArguments.Should().Contain("Jakarta");
    }

    [Fact]
    public void FromResponseBody_returns_error_when_no_candidates()
    {
        var json = """{"candidates":[],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":0}}""";

        var result = _sut.FromResponseBody(json, "gemini-2.0-flash", TimeSpan.Zero);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("no candidates");
    }

    [Fact]
    public void FromResponseBody_returns_error_on_blocked_content()
    {
        var json = """
        {
            "promptFeedback": {"blockReason": "SAFETY"},
            "candidates": [],
            "usageMetadata": {"promptTokenCount": 5, "candidatesTokenCount": 0}
        }
        """;

        var result = _sut.FromResponseBody(json, "gemini-2.0-flash", TimeSpan.Zero);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("blocked");
    }

    [Fact]
    public void FromResponseBody_returns_error_on_safety_finish_reason()
    {
        var json = """
        {
            "candidates": [{
                "content": {"role": "model", "parts": []},
                "finishReason": "SAFETY",
                "safetyRatings": [
                    {"category": "HARM_CATEGORY_DANGEROUS_CONTENT", "probability": "HIGH", "blocked": true}
                ]
            }],
            "usageMetadata": {"promptTokenCount": 5, "candidatesTokenCount": 0}
        }
        """;

        var result = _sut.FromResponseBody(json, "gemini-2.0-flash", TimeSpan.Zero);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("safety");
    }

    [Fact]
    public void FromResponseBody_does_not_expose_upstream_block_reason_or_safety_category()
    {
        const string marker = "provider-secret-account-diagnostic";
        var json = $"{{\"promptFeedback\":{{\"blockReason\":\"{marker}\"}},\"candidates\":[{{\"finishReason\":\"SAFETY\",\"safetyRatings\":[{{\"category\":\"{marker}\",\"blocked\":true}}]}}]}}";

        var promptResult = _sut.FromResponseBody(json, "gemini-2.0-flash", TimeSpan.Zero);
        promptResult.ErrorMessage.Should().NotContain(marker);
        promptResult.ErrorMessage.Should().Be("Gemini: content blocked by provider safety policy.");

        var safetyJson = "{\"candidates\":[{\"finishReason\":\"SAFETY\",\"safetyRatings\":[{\"category\":\"" + marker + "\",\"blocked\":true}]}]}";
        var safetyResult = _sut.FromResponseBody(safetyJson, "gemini-2.0-flash", TimeSpan.Zero);
        safetyResult.ErrorMessage.Should().NotContain(marker);
        safetyResult.ErrorMessage.Should().Be("Gemini: response blocked by safety policy.");

        var unknownResult = _sut.FromResponseBody("{\"candidates\":[{\"finishReason\":\"PROVIDER_PRIVATE_REASON\",\"content\":{\"parts\":[{\"text\":\"must not be returned\"}]}}]}", "gemini-2.0-flash", TimeSpan.Zero);
        unknownResult.IsSuccess.Should().BeFalse();
        unknownResult.ErrorMessage.Should().Be("Gemini: response blocked by provider policy.");
    }


    [Fact]
    public void FromResponseBody_returns_error_on_malformed_json()
    {
        var result = _sut.FromResponseBody("{bad json}", "gemini-2.0-flash", TimeSpan.Zero);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("Gemini: invalid JSON response.");
    }

    [Fact]
    public void ToRequestBody_preserves_specific_tool_choice_and_exact_gemini_keys()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gemini-2.0-flash",
            Messages = [new() { Role = "user", Content = "call it" }],
            ToolChoice = CanonicalToolChoice.ForTool("get_weather"),
        };

        var json = Serialize(_sut.ToRequestBody(req));

        json.Should().Contain("\"toolConfig\"");
        json.Should().Contain("\"functionCallingConfig\"");
        json.Should().Contain("\"allowedFunctionNames\"");
        json.Should().NotContain("tool_config");
        json.Should().NotContain("system_instruction");
    }

    [Fact]
    public void ToRequestBody_serializes_assistant_tool_calls_as_model_and_preserves_function_name()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gemini-2.0-flash",
            Messages =
            [
                new()
                {
                    Role = "assistant",
                    ToolCalls =
                    [new() { Id = "call-1", Name = "get_weather", Arguments = "{\"city\":\"Jakarta\"}" }],
                },
                new() { Role = "tool", ToolCallId = "gemini:get_weather:0", Content = "{\"temp\":30}" },
            ],
        };

        var json = Serialize(_sut.ToRequestBody(req));

        json.Should().Contain("\"role\":\"model\"");
        json.Should().Contain("functionCall");
        json.Should().Contain("functionResponse");
        json.Should().Contain("\"name\":\"get_weather\"");
    }
}
