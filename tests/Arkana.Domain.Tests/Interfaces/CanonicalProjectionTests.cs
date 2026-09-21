using System.Text.Json;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using FluentAssertions;
using Xunit;

namespace Arkana.Domain.Tests.Interfaces;

/// <summary>
/// Pins the projection from the historical <see cref="ChatRequest"/>
/// (OpenAI-flavored) to the provider-neutral <see cref="CanonicalChatRequest"/>.
/// These tests are the contract for the dialect translator that lands in
/// 14b-1 — if the projection changes, every dialect breaks.
/// </summary>
public class CanonicalProjectionTests
{
    [Fact]
    public void ChatRequest_ToCanonical_preserves_model_and_user()
    {
        var req = new ChatRequest { Model = "gpt-4o", UserId = "u-1" };

        var canonical = req.ToCanonical();

        canonical.Model.Should().Be("gpt-4o");
        canonical.UserId.Should().Be("u-1");
    }

    [Fact]
    public void ChatRequest_ToCanonical_preserves_message_stream_in_order()
    {
        var req = new ChatRequest
        {
            Model = "gpt-4o",
            Messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = "You are a helpful assistant." },
                new() { Role = "user", Content = "Hi" },
                new() { Role = "assistant", Content = "Hello!" },
            }
        };

        var canonical = req.ToCanonical();

        canonical.Messages.Should().HaveCount(3);
        canonical.Messages[0].Role.Should().Be("system");
        canonical.Messages[0].Content.Should().Be("You are a helpful assistant.");
        canonical.Messages[1].Role.Should().Be("user");
        canonical.Messages[2].Role.Should().Be("assistant");
    }

    [Fact]
    public void ChatMessage_ToCanonical_preserves_tool_call_id()
    {
        var msg = new ChatMessage
        {
            Role = "tool",
            Content = "{\"temp\": 22}",
            ToolCallId = "call_abc"
        };

        var canonical = msg.ToCanonical();

        canonical.Role.Should().Be("tool");
        canonical.ToolCallId.Should().Be("call_abc");
        canonical.Content.Should().Be("{\"temp\": 22}");
        canonical.ToolCalls.Should().BeNull();
    }

    [Fact]
    public void ChatMessage_ToCanonical_flattens_tool_calls_to_canonical_shape()
    {
        var msg = new ChatMessage
        {
            Role = "assistant",
            Content = "",
            ToolCalls = new List<ToolCall>
            {
                new()
                {
                    Id = "call_1",
                    Type = "function",
                    Function = new ToolCallFunction { Name = "get_weather", Arguments = "{\"city\":\"Jakarta\"}" }
                }
            }
        };

        var canonical = msg.ToCanonical();

        canonical.ToolCalls.Should().NotBeNull();
        canonical.ToolCalls!.Should().HaveCount(1);
        var call = canonical.ToolCalls[0];
        call.Id.Should().Be("call_1");
        call.Kind.Should().Be(CanonicalToolCall.ToolKind.Function);
        call.Name.Should().Be("get_weather");
        call.Arguments.Should().Be("{\"city\":\"Jakarta\"}");
    }

    [Fact]
    public void ToolDefinition_ToCanonical_preserves_parameters_as_json_element()
    {
        // Parameters must survive the projection as JsonElement, not be re-serialized.
        var json = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": { "city": { "type": "string" } },
          "required": ["city"]
        }
        """).RootElement;

        var def = new ToolDefinition
        {
            Type = "function",
            Function = new ToolFunction
            {
                Name = "get_weather",
                Description = "Get current weather",
                Parameters = json,
                Strict = true,
            }
        };

        var canonical = def.ToCanonical();

        canonical.Function.Name.Should().Be("get_weather");
        canonical.Function.Description.Should().Be("Get current weather");
        canonical.Function.Strict.Should().BeTrue();
        canonical.Function.Parameters.Should().NotBeNull();
        canonical.Function.Parameters!.Value.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public void ToolDefinition_ToCanonical_handles_null_description_and_parameters()
    {
        var def = new ToolDefinition
        {
            Type = "function",
            Function = new ToolFunction { Name = "ping" }
        };

        var canonical = def.ToCanonical();

        canonical.Function.Description.Should().BeNull();
        canonical.Function.Parameters.Should().BeNull();
        canonical.Function.Strict.Should().BeNull();
    }

    [Fact]
    public void CanonicalUsage_Total_falls_back_to_sum_when_provider_omits_it()
    {
        var usage = new CanonicalUsage { InputTokens = 10, OutputTokens = 20 };

        usage.Total.Should().Be(30);
    }

    [Fact]
    public void CanonicalUsage_Total_uses_explicit_total_when_provider_reports_it()
    {
        var usage = new CanonicalUsage { InputTokens = 10, OutputTokens = 20, TotalTokens = 50 };

        // Some providers report a Total that diverges from input+output
        // (e.g. Anthropic sometimes). Trust the explicit value.
        usage.Total.Should().Be(50);
    }

    [Fact]
    public void CanonicalToolChoice_presets_have_correct_mode()
    {
        CanonicalToolChoice.AutoValue.ChoiceMode.Should().Be(CanonicalToolChoice.Mode.Auto);
        CanonicalToolChoice.AnyValue.ChoiceMode.Should().Be(CanonicalToolChoice.Mode.Any);
        CanonicalToolChoice.NoneValue.ChoiceMode.Should().Be(CanonicalToolChoice.Mode.None);
    }

    [Fact]
    public void CanonicalToolChoice_ForTool_sets_specific_mode_with_name()
    {
        var choice = CanonicalToolChoice.ForTool("get_weather");

        choice.ChoiceMode.Should().Be(CanonicalToolChoice.Mode.Specific);
        choice.ToolName.Should().Be("get_weather");
    }

    [Fact]
    public void ChatRequest_ToCanonical_defaults_tool_choice_to_auto()
    {
        // Today ChatRequest.ToolChoice is object? — the projection defaults
        // to Auto until 14b-1 lands a proper object? -> CanonicalToolChoice
        // mapping. The default must be safe (Auto), not None.
        var req = new ChatRequest { Model = "gpt-4o", ToolChoice = null };

        var canonical = req.ToCanonical();

        canonical.ToolChoice.Should().NotBeNull();
        canonical.ToolChoice!.ChoiceMode.Should().Be(CanonicalToolChoice.Mode.Auto);
    }

    [Fact]
    public void ChatRequest_ToCanonical_preserves_string_tool_choice()
    {
        var req = new ChatRequest { Model = "gemini-2.0-flash", ToolChoice = "none" };

        req.ToCanonical().ToolChoice!.ChoiceMode.Should().Be(CanonicalToolChoice.Mode.None);
    }

    [Fact]
    public void ChatRequest_ToCanonical_preserves_named_function_tool_choice()
    {
        using var doc = JsonDocument.Parse("{\"type\":\"function\",\"function\":{\"name\":\"get_weather\"}}");
        var req = new ChatRequest { Model = "gemini-2.0-flash", ToolChoice = doc.RootElement.Clone() };

        var choice = req.ToCanonical().ToolChoice!;
        choice.ChoiceMode.Should().Be(CanonicalToolChoice.Mode.Specific);
        choice.ToolName.Should().Be("get_weather");
    }

    [Fact]
    public void ChatRequest_ToCanonical_handles_empty_message_list()
    {
        var req = new ChatRequest { Model = "gpt-4o" };

        var canonical = req.ToCanonical();

        canonical.Messages.Should().BeEmpty();
        canonical.Tools.Should().BeNull();
    }

    [Fact]
    public void ChatRequest_ToCanonical_preserves_tool_definitions()
    {
        var req = new ChatRequest
        {
            Model = "gpt-4o",
            Tools = new List<ToolDefinition>
            {
                new() { Type = "function", Function = new ToolFunction { Name = "a" } },
                new() { Type = "function", Function = new ToolFunction { Name = "b" } },
            }
        };

        var canonical = req.ToCanonical();

        canonical.Tools.Should().NotBeNull();
        canonical.Tools!.Select(t => t.Function.Name).Should().Equal("a", "b");
    }
}
