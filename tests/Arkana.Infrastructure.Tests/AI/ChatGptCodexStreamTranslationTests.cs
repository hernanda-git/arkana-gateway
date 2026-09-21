using System.Text;
using Arkana.Infrastructure.AI;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkana.Infrastructure.Tests.AI;

/// <summary>
/// Regression tests for the "sol stream kepotong diam-diam" bug (2026-08-24):
/// the ChatGPT Codex SSE translator mapped every terminal condition to a clean
/// finish_reason="stop", so clients could not distinguish a complete answer
/// from one cut by max_output_tokens, an upstream failure, or a premature
/// connection close. These tests pin the new contract:
///   - response.incomplete        -> finish_reason "length" + Failed=true
///   - response.failed            -> explicit error payload + Failed=true
///   - premature upstream EOF     -> synthetic finish (stop/tool_calls) + Warning logged + Failed=true
///   - normal response.completed  -> stop/tool_calls + Failed=false
/// </summary>
public sealed class ChatGptCodexStreamTranslationTests
{
    private static (string Output, Func<bool> FailedProbe) Run(
        string upstreamSse, CancellationToken? ct = null)
    {
        var token = ct ?? CancellationToken.None;
        var upstream = new MemoryStream(Encoding.UTF8.GetBytes(upstreamSse));
        var (outputStream, failedProbe) = ChatGptCodexChatService.CreateTranslatingStreamForTests(
            upstream, NullLogger.Instance, token);
        using var reader = new StreamReader(outputStream, Encoding.UTF8);
        var text = reader.ReadToEndAsync().GetAwaiter().GetResult();
        return (text, failedProbe);
    }

    private static string[] FinishReasons(string translated)
    {
        var reasons = new List<string>();
        foreach (var line in translated.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal) || line.Contains("[DONE]")) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line[6..]);
                if (doc.RootElement.TryGetProperty("choices", out var c) && c.GetArrayLength() > 0
                    && c[0].TryGetProperty("finish_reason", out var fr) && fr.ValueKind == System.Text.Json.JsonValueKind.String)
                    reasons.Add(fr.GetString()!);
            }
            catch { /* skip non-choice frames */ }
        }
        return reasons.ToArray();
    }

    [Fact]
    public void ResponseCompleted_MapsTo_Stop_AndNotFailed()
    {
        var sse = "event: response.output_text.delta\n" +
                  "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}\n\n" +
                  "event: response.completed\n" +
                  "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":3,\"output_tokens\":2}}}\n\n";

        var (output, probe) = Run(sse);

        FinishReasons(output).Should().Equal("stop");
        output.Should().Contain("\"content\":\"hello\"");
        probe().Should().BeFalse();
    }

    [Fact]
    public void ResponseIncomplete_MapsTo_Length_AndFailed()
    {
        var sse = "event: response.output_text.delta\n" +
                  "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial answer\"}\n\n" +
                  "event: response.incomplete\n" +
                  "data: {\"type\":\"response.incomplete\",\"response\":{\"usage\":{\"input_tokens\":10,\"output_tokens\":900}}}\n\n";

        var (output, probe) = Run(sse);

        // The old behaviour emitted finish_reason="stop" here — the exact cause
        // of silent mid-generation truncation for reasoning models.
        FinishReasons(output).Should().Contain("length")
            .And.NotContain("stop");
        output.Should().Contain("prompt_tokens\":10");
        probe().Should().BeTrue();
    }

    [Fact]
    public void ResponseFailed_EmitsErrorPayload_AndFailed()
    {
        var sse = "event: response.output_text.delta\n" +
                  "data: {\"type\":\"response.output_text.delta\",\"delta\":\"so far so good\"}\n\n" +
                  "event: response.failed\n" +
                  "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"server exploded\"}}}\n\n";

        var (output, probe) = Run(sse);

        output.Should().Contain("server exploded");
        output.Should().Contain("upstream_error");
        probe().Should().BeTrue();
        // A failed turn must not be reported as a clean stop.
        FinishReasons(output).Should().NotContain("length");
    }

    [Fact]
    public void PrematureUpstreamEof_EmitsSyntheticFinish_AndFailed()
    {
        // Stream cut before any terminal event — previously completely silent.
        var sse = "event: response.output_text.delta\n" +
                  "data: {\"type\":\"response.output_text.delta\",\"delta\":\"half an answe\"}";

        var (output, probe) = Run(sse);

        FinishReasons(output).Should().Equal("stop"); // synthetic finish present
        probe().Should().BeTrue();
    }

    [Fact]
    public void PrematureUpstreamEof_AfterFunctionCall_FinishesWithToolCalls()
    {
        var sse = "event: response.output_item.added\n" +
                  "data: {\"type\":\"response.output_item.added\",\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"name\":\"bash\"}}\n\n" +
                  "event: response.function_call_arguments.delta\n" +
                  "data: {\"type\":\"response.function_call_arguments.delta\",\"delta\":\"{\\\"cmd\\\":\\\"ls\\\"}\"}";

        var (output, probe) = Run(sse);

        FinishReasons(output).Should().Equal("tool_calls");
        output.Should().Contain("bash");
        probe().Should().BeTrue();
    }

    [Fact]
    public void CompletedAfterToolCall_Finishes_ToolCalls_NotFailed()
    {
        var sse = "event: response.output_item.added\n" +
                  "data: {\"type\":\"response.output_item.added\",\"item\":{\"id\":\"fc_9\",\"type\":\"function_call\",\"name\":\"read\"}}\n\n" +
                  "event: response.completed\n" +
                  "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":5,\"output_tokens\":7}}}\n\n";

        var (output, probe) = Run(sse);

        FinishReasons(output).Should().Equal("tool_calls");
        probe().Should().BeFalse();
    }
}
