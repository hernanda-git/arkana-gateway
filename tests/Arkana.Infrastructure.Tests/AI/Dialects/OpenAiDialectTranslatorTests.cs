using System.Text.Json;
using System.Text.Encodings.Web;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Infrastructure.AI.Dialects;
using FluentAssertions;
using Xunit;

namespace Arkana.Infrastructure.Tests.AI.Dialects;

/// <summary>
/// Pins the wire format produced by <see cref="OpenAiDialectTranslator"/>.
/// These are golden tests — they lock the JSON shape so that refactoring
/// the four OpenAI-compat connectors in 14b-2 cannot silently change what
/// hits the wire. A real regression test (end-to-end against the live API)
/// lives in the integration suite; these tests are the unit-level guarantee.
/// </summary>
public class OpenAiDialectTranslatorTests
{
    private readonly OpenAiDialectTranslator _sut = new();

    /// <summary>
    /// Test-only serializer with relaxed escaping so assertions can
    /// match on raw inner JSON in tool-call arguments. The translator
    /// itself uses the strict default; this is purely for readable
    /// expectation strings.
    /// </summary>
    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Serialize(Dictionary<string, object?> body) =>
        JsonSerializer.Serialize(body, ReadableJson);

    // ── request side ──────────────────────────────────────────────

    [Fact]
    public void ToRequestBody_includes_model_and_messages_in_order()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gpt-4o",
            Messages =
            [
                new() { Role = "system", Content = "be brief" },
                new() { Role = "user", Content = "hi" },
            ],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"model\":\"gpt-4o\"");
        json.Should().Contain("\"role\":\"system\"");
        json.Should().Contain("\"role\":\"user\"");
        json.Should().Contain("\"stream\":false");
    }

    [Fact]
    public void ToRequestBody_defaults_model_when_empty()
    {
        var req = new CanonicalChatRequest
        {
            Model = string.Empty,
            Messages = [new() { Role = "user", Content = "hi" }],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"model\":\"gpt-4o-mini\"");
    }

    [Fact]
    public void ToRequestBody_omits_messages_content_when_empty_string()
    {
        // OpenAI rejects empty-string content on most message roles;
        // omit it instead of sending "".
        var req = new CanonicalChatRequest
        {
            Model = "gpt-4o",
            Messages = [new() { Role = "assistant", Content = "" }],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        // JsonOpts has DefaultIgnoreCondition.WhenWritingNull, so a null content
        // is dropped from the wire. The translator sends null (not "") for empty.
        json.Should().NotContain("\"content\":\"\"");
    }

    [Fact]
    public void ToRequestBody_includes_tool_call_id_for_tool_role()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gpt-4o",
            Messages =
            [
                new() { Role = "assistant", Content = "", ToolCalls =
                [
                    new() { Id = "call_1", Name = "get_weather", Arguments = "{}" }
                ]},
                new() { Role = "tool", Content = "{\"temp\":22}", ToolCallId = "call_1" },
            ],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"tool_call_id\":\"call_1\"");
    }

    [Fact]
    public void ToRequestBody_serializes_assistant_tool_calls_with_id_type_function()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gpt-4o",
            Messages =
            [
                new() { Role = "assistant", Content = "", ToolCalls =
                [
                    new() { Id = "call_1", Name = "get_weather", Arguments = "{\"city\":\"Jakarta\"}" }
                ]},
            ],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"id\":\"call_1\"");
        json.Should().Contain("\"type\":\"function\"");
        json.Should().Contain("\"name\":\"get_weather\"");
        json.Should().Contain("\"arguments\":\"{\\\"city\\\":\\\"Jakarta\\\"}\"");
    }

    [Fact]
    public void ToRequestBody_omits_tools_section_when_null()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gpt-4o",
            Messages = [new() { Role = "user", Content = "hi" }],
            Tools = null,
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().NotContain("\"tools\"");
    }

    [Fact]
    public void ToRequestBody_includes_tool_definitions_when_present()
    {
        var schema = JsonDocument.Parse("""
        { "type": "object", "properties": { "city": { "type": "string" } } }
        """).RootElement;

        var req = new CanonicalChatRequest
        {
            Model = "gpt-4o",
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
                        Strict = true,
                    }
                }
            }
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"type\":\"function\"");
        json.Should().Contain("\"name\":\"get_weather\"");
        json.Should().Contain("\"description\":\"Get current weather\"");
        json.Should().Contain("\"strict\":true");
    }

    [Fact]
    public void ToRequestBody_maps_auto_tool_choice_to_string_auto()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gpt-4o",
            Messages = [new() { Role = "user", Content = "hi" }],
            Tools = new List<CanonicalToolDefinition>
            {
                new() { Function = new CanonicalToolFunction { Name = "ping" } }
            },
            ToolChoice = CanonicalToolChoice.AutoValue,
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"tool_choice\":\"auto\"");
    }

    [Fact]
    public void ToRequestBody_maps_any_tool_choice_to_required()
    {
        // OpenAI's "any" semantics map to the literal string "required".
        var req = new CanonicalChatRequest
        {
            Model = "gpt-4o",
            Messages = [new() { Role = "user", Content = "hi" }],
            Tools = new List<CanonicalToolDefinition>
            {
                new() { Function = new CanonicalToolFunction { Name = "ping" } }
            },
            ToolChoice = CanonicalToolChoice.AnyValue,
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"tool_choice\":\"required\"");
    }

    [Fact]
    public void ToRequestBody_maps_none_tool_choice_to_string_none()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gpt-4o",
            Messages = [new() { Role = "user", Content = "hi" }],
            Tools = new List<CanonicalToolDefinition>
            {
                new() { Function = new CanonicalToolFunction { Name = "ping" } }
            },
            ToolChoice = CanonicalToolChoice.NoneValue,
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"tool_choice\":\"none\"");
    }

    [Fact]
    public void ToRequestBody_maps_specific_tool_choice_to_object_form()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gpt-4o",
            Messages = [new() { Role = "user", Content = "hi" }],
            Tools = new List<CanonicalToolDefinition>
            {
                new() { Function = new CanonicalToolFunction { Name = "get_weather" } }
            },
            ToolChoice = CanonicalToolChoice.ForTool("get_weather"),
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        // OpenAI's "force a specific tool" shape is a nested object.
        json.Should().Contain("\"type\":\"function\"");
        json.Should().Contain("\"name\":\"get_weather\"");
    }

    [Fact]
    public void ToRequestBody_includes_user_field_when_present()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gpt-4o",
            UserId = "u-42",
            Messages = [new() { Role = "user", Content = "hi" }],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        json.Should().Contain("\"user\":\"u-42\"");
    }

    [Fact]
    public void ToRequestBody_omits_user_field_when_null()
    {
        var req = new CanonicalChatRequest
        {
            Model = "gpt-4o",
            UserId = null,
            Messages = [new() { Role = "user", Content = "hi" }],
        };

        var body = _sut.ToRequestBody(req);
        var json = Serialize(body);

        // Match the JSON key (":\"user\"" at a key position) — not the role
        // value "user" which appears in the messages array.
        json.Should().NotContain("\"user\":");
    }

    // ── response side ─────────────────────────────────────────────

    [Fact]
    public void FromResponseBody_parses_text_response()
    {
        var json = """
        {
          "id": "chatcmpl-1",
          "model": "gpt-4o-mini",
          "choices": [
            { "index": 0, "message": { "role": "assistant", "content": "Hello!" } }
          ],
          "usage": { "prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15 }
        }
        """;

        var result = _sut.FromResponseBody(json, "gpt-4o-mini", TimeSpan.FromMilliseconds(120));

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Be("Hello!");
        result.Model.Should().Be("gpt-4o-mini");
        result.InputTokens.Should().Be(10);
        result.OutputTokens.Should().Be(5);
        result.Duration.Should().Be(TimeSpan.FromMilliseconds(120));
        result.ToolCalls.Should().BeNull();
    }

    [Fact]
    public void FromResponseBody_parses_tool_calls()
    {
        var json = """
        {
          "id": "chatcmpl-2",
          "model": "gpt-4o",
          "choices": [
            {
              "index": 0,
              "message": {
                "role": "assistant",
                "content": null,
                "tool_calls": [
                  {
                    "id": "call_abc",
                    "type": "function",
                    "function": { "name": "get_weather", "arguments": "{\"city\":\"SF\"}" }
                  }
                ]
              }
            }
          ],
          "usage": { "prompt_tokens": 20, "completion_tokens": 30, "total_tokens": 50 }
        }
        """;

        var result = _sut.FromResponseBody(json, "gpt-4o", TimeSpan.Zero);

        result.IsSuccess.Should().BeTrue();
        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls![0].Id.Should().Be("call_abc");
        result.ToolCalls[0].Type.Should().Be("function");
        result.ToolCalls[0].FunctionName.Should().Be("get_weather");
        result.ToolCalls[0].FunctionArguments.Should().Be("{\"city\":\"SF\"}");
    }

    [Fact]
    public void FromResponseBody_falls_back_to_requested_model_when_response_omits_it()
    {
        var json = """
        {
          "choices": [{ "index": 0, "message": { "role": "assistant", "content": "ok" } }]
        }
        """;

        var result = _sut.FromResponseBody(json, "requested-model", TimeSpan.Zero);

        result.Model.Should().Be("requested-model");
    }

    [Fact]
    public void FromResponseBody_returns_error_on_malformed_json()
    {
        var result = _sut.FromResponseBody("{ not json", "gpt-4o", TimeSpan.Zero);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("OpenAI dialect: invalid JSON response.");
    }

    [Fact]
    public void FromResponseBody_returns_error_on_null_response_body()
    {
        var result = _sut.FromResponseBody("null", "gpt-4o", TimeSpan.Zero);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("empty response");
    }

    [Fact]
    public void FromResponseBody_treats_empty_choices_as_empty_content()
    {
        var json = """
        { "id": "x", "model": "gpt-4o", "choices": [], "usage": null }
        """;

        var result = _sut.FromResponseBody(json, "gpt-4o", TimeSpan.Zero);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().BeEmpty();
        result.InputTokens.Should().Be(0);
        result.OutputTokens.Should().Be(0);
        result.ToolCalls.Should().BeNull();
    }

    [Fact]
    public void FromResponseBody_treats_missing_choices_as_empty_content()
    {
        var json = """{ "id": "x", "model": "gpt-4o" }""";

        var result = _sut.FromResponseBody(json, "gpt-4o", TimeSpan.Zero);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().BeEmpty();
    }

    [Fact]
    public void FromResponseBody_treats_empty_tool_call_name_and_args_as_empty_strings()
    {
        // Defensive: if upstream sends a tool_call with null fields, the
        // result must not NRE. Empty string is a safe default.
        var json = """
        {
          "choices": [
            {
              "index": 0,
              "message": {
                "role": "assistant",
                "content": null,
                "tool_calls": [
                  { "id": "c1", "type": "function", "function": { "name": null, "arguments": null } }
                ]
              }
            }
          ]
        }
        """;

        var result = _sut.FromResponseBody(json, "gpt-4o", TimeSpan.Zero);

        result.IsSuccess.Should().BeTrue();
        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls![0].Id.Should().Be("c1");
        result.ToolCalls[0].FunctionName.Should().BeEmpty();
        result.ToolCalls[0].FunctionArguments.Should().BeEmpty();
    }

    [Fact]
    public void DialectCode_is_openai()
    {
        _sut.DialectCode.Should().Be("openai");
    }

    // ── full round-trip via projection (the seam between 14a-1 and 14b-1) ──

    [Fact]
    public void Full_flow_chat_request_to_canonical_to_wire_body()
    {
        // Simulates what an OpenAI-compat connector will do: take a
        // ChatRequest, project to canonical, then ask the translator
        // for the wire body. The wire must be identical to what the
        // connector would have built inline before the refactor.
        var req = new ChatRequest
        {
            Model = "gpt-4o",
            Messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = "be brief" },
                new() { Role = "user", Content = "hi" },
            },
        };

        var canonical = req.ToCanonical();
        var body = _sut.ToRequestBody(canonical);
        var json = Serialize(body);

        json.Should().Contain("\"model\":\"gpt-4o\"");
        json.Should().Contain("\"role\":\"system\"");
        json.Should().Contain("\"content\":\"be brief\"");
        json.Should().Contain("\"role\":\"user\"");
        json.Should().Contain("\"content\":\"hi\"");
        json.Should().Contain("\"stream\":false");
    }
}
