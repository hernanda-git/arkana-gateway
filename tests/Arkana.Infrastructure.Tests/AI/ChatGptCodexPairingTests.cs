using System.Text.Json;
using System.Text.Json.Nodes;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.AI;
using FluentAssertions;
using Xunit;

namespace Arkana.Infrastructure.Tests.AI;

/// <summary>
/// Regression tests for the "user synthetic call_id" bug (2026-08-24):
/// upstream ChatGPT /codex/responses rejects a request whose
/// function_call_output items reference call_ids that don't match any
/// function_call item ("No tool call found for function call output with
/// call_id call_synthetic_*"). ConvertMessagesToResponsesInput must guarantee
/// every output pairs with an emitted function_call carrying the SAME id.
/// </summary>
public sealed class ChatGptCodexPairingTests
{
    private static List<JsonObject> FunctionCalls(JsonArray? input) =>
        (input ?? []).OfType<JsonObject>()
            .Where(o => o["type"]?.GetValue<string>() == "function_call")
            .ToList();

    private static List<JsonObject> Outputs(JsonArray? input) =>
        (input ?? []).OfType<JsonObject>()
            .Where(o => o["type"]?.GetValue<string>() == "function_call_output")
            .ToList();

    private static List<string> FlatTexts(JsonArray? input) =>
        (input ?? []).OfType<JsonObject>()
            .Where(o => o["type"] == null && o["content"] is JsonArray)
            .SelectMany(o => ((JsonArray)o["content"]!).OfType<JsonObject>())
            .Where(p => p["text"] is not null)
            .Select(p => p["text"]!.GetValue<string>())
            .ToList();

    [Fact]
    public void ToolOutput_WithRealCallId_KeepsTheId_AndPairs()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "assistant",
                Content = "",
                ToolCalls = [new ToolCall { Id = "fc_011e951e", Type = "function",
                    Function = new ToolCallFunction { Name = "get_weather", Arguments = "{\"city\":\"Jakarta\"}" } }],
            },
            new() { Role = "tool", Content = "sunny 31C", ToolCallId = "fc_011e951e" },
        };

        var input = ChatGptCodexChatService.ConvertMessagesToResponsesInput(messages);

        var fcs = FunctionCalls(input);
        var outs = Outputs(input);
        fcs.Should().HaveCount(1);
        outs.Should().HaveCount(1);
        // The REAL id survives on both sides — no call_synthetic_* anywhere.
        fcs[0]["call_id"]!.GetValue<string>().Should().Be("fc_011e951e");
        outs[0]["call_id"]!.GetValue<string>().Should().Be("fc_011e951e");
    }

    [Fact]
    public void ToolOutput_MissingCallId_PairsWithPriorRealFunctionCallId()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "assistant",
                Content = "",
                ToolCalls = [new ToolCall { Id = "fc_real_1", Type = "function",
                    Function = new ToolCallFunction { Name = "read_file" } }],
            },
            // Orphan-shaped: no id survived the client round-trip.
            new() { Role = "tool", Content = "file body" },
        };

        var input = ChatGptCodexChatService.ConvertMessagesToResponsesInput(messages);

        var fcs = FunctionCalls(input);
        var outs = Outputs(input);
        fcs.Should().ContainSingle();
        outs.Should().ContainSingle();
        // The output reuses the assistant's real id instead of synthesizing a
        // mismatched one.
        outs[0]["call_id"]!.GetValue<string>().Should().Be("fc_real_1");
    }

    [Fact]
    public void ToolOutput_WrongCallId_FlattensToText_NeverMixesIds()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "assistant",
                Content = "",
                ToolCalls = [new ToolCall { Id = "fc_model_a", Type = "function",
                    Function = new ToolCallFunction { Name = "f" } }],
            },
            // Client echoes a DIFFERENT id (typo / different field convention).
            new() { Role = "tool", Content = "ok", ToolCallId = "fc_client_b" },
        };

        var input = ChatGptCodexChatService.ConvertMessagesToResponsesInput(messages);

        var fcs = FunctionCalls(input);
        var outs = Outputs(input);
        // The backend never issued fc_client_b, so the result is flattened to
        // text and BOTH the mismatched output and now-dangling function_call
        // are dropped — never a half-pair with mixed ids.
        fcs.Should().BeEmpty();
        outs.Should().BeEmpty();
        FlatTexts(input).Should().Contain(t => t.Contains("ok"));
    }

    [Fact]
    public void ParallelCalls_Pair_InOrder()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "assistant",
                Content = "",
                ToolCalls =
                [
                    new ToolCall { Id = "fc_p1", Type = "function", Function = new ToolCallFunction { Name = "a" } },
                    new ToolCall { Id = "fc_p2", Type = "function", Function = new ToolCallFunction { Name = "b" } },
                ],
            },
            new() { Role = "tool", Content = "r1", ToolCallId = "fc_p1" },
            new() { Role = "tool", Content = "r2" }, // missing id → pairs the remaining fc_p2
        };

        var input = ChatGptCodexChatService.ConvertMessagesToResponsesInput(messages);

        var outs = Outputs(input);
        outs.Should().HaveCount(2);
        outs[0]["call_id"]!.GetValue<string>().Should().Be("fc_p1");
        outs[1]["call_id"]!.GetValue<string>().Should().Be("fc_p2");
    }

    [Fact]
    public void TrulyOrphanedOutput_FlattensToText_NoSyntheticIds()
    {
        // No assistant function_call at all — client sends only the tool result.
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "run it" },
            new() { Role = "tool", Content = "done" },
        };

        var input = ChatGptCodexChatService.ConvertMessagesToResponsesInput(messages);

        // No call_id is ever invented: the orphan output becomes plain text.
        FunctionCalls(input).Should().BeEmpty();
        Outputs(input).Should().BeEmpty();
        FlatTexts(input).Should().Contain(t => t.Contains("done"));
    }

    [Fact]
    public void UnansweredFunctionCall_IsDropped_NoDanglingCall()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "assistant",
                Content = "",
                ToolCalls = [new ToolCall { Id = "fc_dangling", Type = "function",
                    Function = new ToolCallFunction { Name = "f" } }],
            },
            // No tool message follows — the call would dangle upstream.
            new() { Role = "user", Content = "never mind" },
        };

        var input = ChatGptCodexChatService.ConvertMessagesToResponsesInput(messages);

        FunctionCalls(input).Should().BeEmpty();
        Outputs(input).Should().BeEmpty();
    }

    [Fact]
    public void PlainConversation_IsUnchanged_NoToolItems()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = "be terse" },
            new() { Role = "user", Content = "hi" },
        };

        var input = ChatGptCodexChatService.ConvertMessagesToResponsesInput(messages);

        FunctionCalls(input).Should().BeEmpty();
        Outputs(input).Should().BeEmpty();
        input!.Count.Should().Be(2);
    }

    // ---- BUG FIX #3 (2026-08-26, tooluse-id) -------------------------
    // ChatGPT /codex/responses rejects ids not beginning with "fc"
    // ("Invalid 'input[3].id': 'tooluse_…'. Expected an ID that begins with
    // 'fc'"). Clients like Continue.dev replay Anthropic-style tooluse_* ids.

    [Fact]
    public void ClientIssuedToolUseId_IsRemappedToFcPrefix_OnBothPairSides()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "assistant",
                Content = "",
                ToolCalls = [new ToolCall { Id = "tooluse_aDk0CdeI8B2tqe55r5lj4g", Type = "function",
                    Function = new ToolCallFunction { Name = "get_weather", Arguments = "{\"city\":\"Jakarta\"}" } }],
            },
            new() { Role = "tool", Content = "sunny 31C", ToolCallId = "tooluse_aDk0CdeI8B2tqe55r5lj4g" },
        };

        var input = ChatGptCodexChatService.ConvertMessagesToResponsesInput(messages);

        var fcs = FunctionCalls(input);
        var outs = Outputs(input);
        fcs.Should().HaveCount(1);
        outs.Should().HaveCount(1);
        // Both sides carry the SAME rewritten fc-prefixed id.
        var callId = fcs[0]["id"]!.GetValue<string>();
        callId.Should().StartWith("fc");
        outs[0]["call_id"]!.GetValue<string>().Should().Be(callId);
        fcs[0]["call_id"]!.GetValue<string>().Should().Be(callId);
        // No raw client id leaks through anywhere.
        input!.ToJsonString().Should().NotContain("tooluse_");
    }

    [Fact]
    public void MultipleClientIds_EachGetDistinctConsistentMappings()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "assistant", Content = "",
                ToolCalls =
                [
                    new ToolCall { Id = "tooluse_AAA", Type = "function",
                        Function = new ToolCallFunction { Name = "f1", Arguments = "{}" } },
                    new ToolCall { Id = "tooluse_BBB", Type = "function",
                        Function = new ToolCallFunction { Name = "f2", Arguments = "{}" } },
                ],
            },
            new() { Role = "tool", Content = "r1", ToolCallId = "tooluse_BBB" },
            new() { Role = "tool", Content = "r2", ToolCallId = "tooluse_AAA" },
        };

        var input = ChatGptCodexChatService.ConvertMessagesToResponsesInput(messages);

        var fcs = FunctionCalls(input);
        var outs = Outputs(input);
        fcs.Select(f => f["id"]!.GetValue<string>()).Should().OnlyContain(id => id.StartsWith("fc"));
        fcs.Select(f => f["id"]!.GetValue<string>()).Should().OnlyHaveUniqueItems();
        // Cross pairing preserved: BBB→r1, AAA→r2 (mapping is deterministic).
        outs.Single(o => o["output"]!.GetValue<string>() == "r1")["call_id"]!.GetValue<string>()
            .Should().Be(fcs.Single(f => f["name"]!.GetValue<string>() == "f2")["id"]!.GetValue<string>());
        outs.Single(o => o["output"]!.GetValue<string>() == "r2")["call_id"]!.GetValue<string>()
            .Should().Be(fcs.Single(f => f["name"]!.GetValue<string>() == "f1")["id"]!.GetValue<string>());
    }

    [Fact]
    public void RealUpstreamFcIds_AreNeverRewritten()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "assistant", Content = "",
                ToolCalls = [new ToolCall { Id = "fc_011e951e", Type = "function",
                    Function = new ToolCallFunction { Name = "f1", Arguments = "{}" } }],
            },
            new() { Role = "tool", Content = "ok", ToolCallId = "fc_011e951e" },
        };

        var input = ChatGptCodexChatService.ConvertMessagesToResponsesInput(messages);

        FunctionCalls(input)[0]["id"]!.GetValue<string>().Should().Be("fc_011e951e");
        Outputs(input)[0]["call_id"]!.GetValue<string>().Should().Be("fc_011e951e");
    }
}
