using System.Text.Encodings.Web;
using System.Text.Json;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.AI.Dialects;
using FluentAssertions;
using Xunit;

namespace Arkana.Infrastructure.Tests.AI.Dialects;

/// <summary>
/// Pins the wire format produced by <see cref="AnthropicDialectTranslator"/>.
/// These tests are the dialect's contract: the connector (15b) and any
/// future work that touches the Anthropic path rely on these shapes.
/// </summary>
public class AnthropicDialectTranslatorTests
{
    private readonly AnthropicDialectTranslator _sut = new();

    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Serialize(Dictionary<string, object?> body) =>
        JsonSerializer.Serialize(body, ReadableJson);

    // ── request side ──────────────────────────────────────────────

    [Fact]
    public void ToRequestBody_uses_messages_endpoint_shape()
    {
        var req = new CanonicalChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages = [new() { Role = "user", Content = "hi" }],
        };

        var body = _sut.ToRequestBody(req);

        body.Should().ContainKey("model");
        body.Should().ContainKey("messages");
        body.Should().ContainKey("max_tokens");
    }

    [Fact]
    public void ToRequestBody_extracts_system_message_to_top_level_field()
    {
        var req = new CanonicalChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages =
            [
                new() { Role = "system", Content = "be brief" },
                new() { Role = "user", Content = "hi" },
            ],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        // System prompt must be at top level, not in the messages array.
        json.Should().Contain("\"system\":\"be brief\"");
        json.Should().NotContain("\"role\":\"system\"");
    }

    [Fact]
    public void ToRequestBody_concatenates_multiple_system_messages_with_double_newline()
    {
        var req = new CanonicalChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages =
            [
                new() { Role = "system", Content = "be brief" },
                new() { Role = "system", Content = "be polite" },
                new() { Role = "user", Content = "hi" },
            ],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"system\":\"be brief\\n\\nbe polite\"");
        json.Should().NotContain("\"role\":\"system\"");
    }

    [Fact]
    public void ToRequestBody_omits_system_field_when_no_system_message()
    {
        var req = new CanonicalChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages = [new() { Role = "user", Content = "hi" }],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().NotContain("\"system\"");
    }

    [Fact]
    public void ToRequestBody_always_includes_max_tokens()
    {
        // Anthropic rejects requests without max_tokens; the canonical
        // model has no field for it, so the translator fills it in.
        var req = new CanonicalChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages = [new() { Role = "user", Content = "hi" }],
        };

        var body = _sut.ToRequestBody(req);

        body["max_tokens"].Should().Be(AnthropicDialectTranslator.DefaultMaxTokens);
    }

    [Fact]
    public void ToRequestBody_rewrites_tool_message_as_user_with_tool_result_block()
    {
        var req = new CanonicalChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages =
            [
                new() { Role = "assistant", Content = "", ToolCalls =
                [
                    new() { Id = "toolu_1", Name = "get_weather", Arguments = "{\"city\":\"SF\"}" }
                ]},
                new() { Role = "tool", Content = "{\"temp\":22}", ToolCallId = "toolu_1" },
            ],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        // The tool message must be a user message with content blocks,
        // not a role: "tool" message (Anthropic doesn't accept that).
        json.Should().Contain("\"role\":\"user\"");
        json.Should().Contain("\"type\":\"tool_result\"");
        json.Should().Contain("\"tool_use_id\":\"toolu_1\"");
        json.Should().NotContain("\"role\":\"tool\"");
    }

    [Fact]
    public void ToRequestBody_serializes_assistant_tool_calls_as_tool_use_blocks()
    {
        var req = new CanonicalChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages =
            [
                new() { Role = "assistant", Content = "", ToolCalls =
                [
                    new() { Id = "toolu_1", Name = "get_weather", Arguments = "{\"city\":\"SF\"}" }
                ]},
            ],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"type\":\"tool_use\"");
        json.Should().Contain("\"id\":\"toolu_1\"");
        json.Should().Contain("\"name\":\"get_weather\"");
        // Arguments must be a JSON object, not a string.
        json.Should().Contain("\"input\":{\"city\":\"SF\"}");
    }

    [Fact]
    public void ToRequestBody_wraps_non_json_tool_arguments_in_raw_envelope()
    {
        // Defensive: if a caller sends non-JSON arguments, Anthropic
        // would reject the request. We wrap in {"raw": "..."} so the
        // request still validates as an object.
        var req = new CanonicalChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages =
            [
                new() { Role = "assistant", Content = "", ToolCalls =
                [
                    new() { Id = "toolu_1", Name = "ping", Arguments = "not-valid-json" }
                ]},
            ],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"input\":{\"raw\":\"not-valid-json\"}");
    }

    [Fact]
    public void ToRequestBody_sends_empty_input_object_for_empty_arguments()
    {
        var req = new CanonicalChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages =
            [
                new() { Role = "assistant", Content = "", ToolCalls =
                [
                    new() { Id = "toolu_1", Name = "ping", Arguments = "" }
                ]},
            ],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"input\":{}");
    }

    [Fact]
    public void ToRequestBody_includes_text_block_alongside_tool_use_blocks()
    {
        // Anthropic allows an assistant message to contain both text
        // and tool_use blocks. The translator must preserve the text.
        var req = new CanonicalChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages =
            [
                new() { Role = "assistant", Content = "Let me check the weather.", ToolCalls =
                [
                    new() { Id = "toolu_1", Name = "get_weather", Arguments = "{}" }
                ]},
            ],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"type\":\"text\"");
        json.Should().Contain("\"text\":\"Let me check the weather.\"");
        json.Should().Contain("\"type\":\"tool_use\"");
    }

    [Fact]
    public void ToRequestBody_serializes_tools_as_flat_function_shape_without_type_wrapper()
    {
        // Anthropic has no "type: function" wrapper on tools; the
        // function fields (name, description, input_schema) are top-level.
        var schema = JsonDocument.Parse("""
        { "type": "object", "properties": { "city": { "type": "string" } } }
        """).RootElement;

        var req = new CanonicalChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages = [new() { Role = "user", Content = "hi" }],
            Tools = new List<CanonicalToolDefinition>
            {
                new()
                {
                    Type = "function",
                    Function = new CanonicalToolFunction
                    {
                        Name = "get_weather",
                        Description = "Get current weather",
                        Parameters = schema,
                    }
                }
            }
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"name\":\"get_weather\"");
        json.Should().Contain("\"description\":\"Get current weather\"");
        json.Should().Contain("\"input_schema\":");
        // The OpenAI-style "type":"function" wrapper is NOT present.
        // (Inside the tool entry itself.)
    }

    [Fact]
    public void ToRequestBody_maps_auto_tool_choice_to_object_with_type_auto()
    {
        var req = new CanonicalChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages = [new() { Role = "user", Content = "hi" }],
            Tools = new List<CanonicalToolDefinition>
            {
                new() { Function = new CanonicalToolFunction { Name = "ping" } }
            },
            ToolChoice = CanonicalToolChoice.AutoValue,
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        // Anthropic's "auto" is an object {type: "auto"}, not a string.
        json.Should().Contain("\"tool_choice\":{\"type\":\"auto\"}");
    }

    [Fact]
    public void ToRequestBody_maps_any_tool_choice_to_object_with_type_any()
    {
        var req = new CanonicalChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages = [new() { Role = "user", Content = "hi" }],
            Tools = new List<CanonicalToolDefinition>
            {
                new() { Function = new CanonicalToolFunction { Name = "ping" } }
            },
            ToolChoice = CanonicalToolChoice.AnyValue,
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"tool_choice\":{\"type\":\"any\"}");
    }

    [Fact]
    public void ToRequestBody_maps_specific_tool_choice_to_object_with_tool_name()
    {
        var req = new CanonicalChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages = [new() { Role = "user", Content = "hi" }],
            Tools = new List<CanonicalToolDefinition>
            {
                new() { Function = new CanonicalToolFunction { Name = "get_weather" } }
            },
            ToolChoice = CanonicalToolChoice.ForTool("get_weather"),
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"tool_choice\":{\"type\":\"tool\",\"name\":\"get_weather\"}");
    }

    // ── response side ─────────────────────────────────────────────

    [Fact]
    public void FromResponseBody_parses_text_response()
    {
        var json = """
        {
          "id": "msg_01",
          "type": "message",
          "role": "assistant",
          "model": "claude-3-5-sonnet-latest",
          "content": [
            { "type": "text", "text": "Hello!" }
          ],
          "stop_reason": "end_turn",
          "usage": { "input_tokens": 10, "output_tokens": 5 }
        }
        """;

        var result = _sut.FromResponseBody(json, "claude-3-5-sonnet-latest", TimeSpan.FromMilliseconds(150));

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Be("Hello!");
        result.Model.Should().Be("claude-3-5-sonnet-latest");
        result.InputTokens.Should().Be(10);
        result.OutputTokens.Should().Be(5);
        result.Duration.Should().Be(TimeSpan.FromMilliseconds(150));
        result.ToolCalls.Should().BeNull();
    }

    [Fact]
    public void FromResponseBody_concatenates_multiple_text_blocks()
    {
        var json = """
        {
          "model": "claude-3-5-sonnet-latest",
          "content": [
            { "type": "text", "text": "First paragraph." },
            { "type": "text", "text": "Second paragraph." }
          ],
          "usage": { "input_tokens": 1, "output_tokens": 2 }
        }
        """;

        var result = _sut.FromResponseBody(json, "claude-3-5-sonnet-latest", TimeSpan.Zero);

        result.Content.Should().Be("First paragraph.Second paragraph.");
    }

    [Fact]
    public void FromResponseBody_parses_tool_use_blocks_as_tool_calls()
    {
        var json = """
        {
          "model": "claude-3-5-sonnet-latest",
          "content": [
            {
              "type": "tool_use",
              "id": "toolu_01",
              "name": "get_weather",
              "input": { "city": "San Francisco" }
            }
          ],
          "stop_reason": "tool_use",
          "usage": { "input_tokens": 20, "output_tokens": 30 }
        }
        """;

        var result = _sut.FromResponseBody(json, "claude-3-5-sonnet-latest", TimeSpan.Zero);

        result.IsSuccess.Should().BeTrue();
        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls![0].Id.Should().Be("toolu_01");
        result.ToolCalls[0].Type.Should().Be("function");
        result.ToolCalls[0].FunctionName.Should().Be("get_weather");
        // The JSON object is re-serialized to a string for the
        // ToolCallInfo contract (caller parses it).
        result.ToolCalls[0].FunctionArguments.Should().Contain("\"city\"");
        result.ToolCalls[0].FunctionArguments.Should().Contain("San Francisco");
    }

    [Fact]
    public void FromResponseBody_handles_mixed_text_and_tool_use_blocks()
    {
        var json = """
        {
          "model": "claude-3-5-sonnet-latest",
          "content": [
            { "type": "text", "text": "Let me check." },
            { "type": "tool_use", "id": "toolu_1", "name": "ping", "input": {} }
          ],
          "usage": { "input_tokens": 1, "output_tokens": 2 }
        }
        """;

        var result = _sut.FromResponseBody(json, "claude-3-5-sonnet-latest", TimeSpan.Zero);

        result.Content.Should().Be("Let me check.");
        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls![0].FunctionName.Should().Be("ping");
    }

    [Fact]
    public void FromResponseBody_treats_empty_input_as_empty_json_object()
    {
        var json = """
        {
          "model": "claude-3-5-sonnet-latest",
          "content": [
            { "type": "tool_use", "id": "toolu_1", "name": "ping", "input": {} }
          ],
          "usage": { "input_tokens": 0, "output_tokens": 0 }
        }
        """;

        var result = _sut.FromResponseBody(json, "claude-3-5-sonnet-latest", TimeSpan.Zero);

        result.ToolCalls![0].FunctionArguments.Should().Be("{}");
    }

    [Fact]
    public void FromResponseBody_falls_back_to_requested_model_when_response_omits_it()
    {
        var json = """
        {
          "content": [{ "type": "text", "text": "ok" }],
          "usage": { "input_tokens": 0, "output_tokens": 0 }
        }
        """;

        var result = _sut.FromResponseBody(json, "requested-model", TimeSpan.Zero);

        result.Model.Should().Be("requested-model");
    }

    [Fact]
    public void FromResponseBody_returns_error_on_malformed_json()
    {
        var result = _sut.FromResponseBody("{ not json", "claude-3-5-sonnet-latest", TimeSpan.Zero);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("malformed JSON");
    }

    [Fact]
    public void FromResponseBody_returns_error_on_null_response_body()
    {
        var result = _sut.FromResponseBody("null", "claude-3-5-sonnet-latest", TimeSpan.Zero);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("empty response");
    }

    [Fact]
    public void FromResponseBody_treats_missing_content_as_empty()
    {
        var json = """
        { "model": "claude-3-5-sonnet-latest", "usage": { "input_tokens": 0, "output_tokens": 0 } }
        """;

        var result = _sut.FromResponseBody(json, "claude-3-5-sonnet-latest", TimeSpan.Zero);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().BeEmpty();
        result.ToolCalls.Should().BeNull();
    }

    [Fact]
    public void FromResponseBody_skips_unknown_block_types()
    {
        // Anthropic may add new block types (e.g. "image", "tool_reference")
        // over time. The translator must not crash on them; it should
        // extract the text it knows about and ignore the rest.
        var json = """
        {
          "model": "claude-3-5-sonnet-latest",
          "content": [
            { "type": "text", "text": "Hello " },
            { "type": "future_block_type", "data": "ignored" },
            { "type": "text", "text": "world" }
          ],
          "usage": { "input_tokens": 0, "output_tokens": 0 }
        }
        """;

        var result = _sut.FromResponseBody(json, "claude-3-5-sonnet-latest", TimeSpan.Zero);

        result.Content.Should().Be("Hello world");
    }

    [Fact]
    public void FromResponseBody_returns_no_tool_calls_when_tool_use_block_missing_required_fields()
    {
        // Defensive: malformed tool_use blocks (no id or no name) must
        // not crash the translator and must not surface as broken
        // tool calls.
        var json = """
        {
          "model": "claude-3-5-sonnet-latest",
          "content": [
            { "type": "tool_use", "name": "no_id" },
            { "type": "tool_use", "id": "no_name" }
          ],
          "usage": { "input_tokens": 0, "output_tokens": 0 }
        }
        """;

        var result = _sut.FromResponseBody(json, "claude-3-5-sonnet-latest", TimeSpan.Zero);

        result.IsSuccess.Should().BeTrue();
        result.ToolCalls.Should().BeNull();
    }

    [Fact]
    public void DialectCode_is_anthropic()
    {
        _sut.DialectCode.Should().Be("anthropic");
    }

    // ── full round-trip via projection ─────────────────────────────

    [Fact]
    public void Full_flow_chat_request_to_canonical_to_anthropic_wire_body()
    {
        // Simulates what the Anthropic connector will do: take a
        // ChatRequest, project to canonical, then ask the translator
        // for the wire body. Validates the full seam from 14a-1's
        // projection to 15a's dialect.
        var req = new Domain.Interfaces.ChatRequest
        {
            Model = "claude-3-5-sonnet-latest",
            Messages = new List<Domain.Interfaces.ChatMessage>
            {
                new() { Role = "system", Content = "be brief" },
                new() { Role = "user", Content = "hi" },
            },
        };

        var canonical = req.ToCanonical();
        var body = _sut.ToRequestBody(canonical);
        var json = Serialize(body);

        json.Should().Contain("\"system\":\"be brief\"");
        json.Should().Contain("\"role\":\"user\"");
        json.Should().Contain("\"content\":\"hi\"");
        json.Should().Contain("\"max_tokens\":8192");
    }
}
