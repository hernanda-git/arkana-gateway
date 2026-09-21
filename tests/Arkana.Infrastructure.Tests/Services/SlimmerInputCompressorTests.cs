using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Arkana.Infrastructure.Tests.Services;

/// <summary>
/// Tests for the Slimmer input compressor (AI-ARKANA-002).
/// Verifies the disabled-by-default no-op behavior, whitespace
/// collapse, the middle-drop trigger threshold, and the
/// pure-function guarantee (same input → same output).
/// </summary>
public sealed class SlimmerInputCompressorTests
{
    private static SlimmerInputCompressor NewCompressor(
        Action<CompressionOptions>? configure = null)
    {
        var opts = new CompressionOptions { InputEnabled = true };
        configure?.Invoke(opts);
        return new SlimmerInputCompressor(
            Options.Create(opts),
            NullLogger<SlimmerInputCompressor>.Instance);
    }

    private static ChatRequest NewRequest(params string[] contents) =>
        new()
        {
            Model = "gpt-4",
            Messages = contents.Select(c => new ChatMessage
            {
                Role = "user",
                Content = c,
            }).ToList(),
        };

    [Fact]
    public void Compress_WhenDisabled_ReturnsRequestUnchanged()
    {
        var c = NewCompressor(o => o.InputEnabled = false);
        var req = NewRequest("hello world");

        var result = c.Compress(req);

        result.Should().BeSameAs(req);
    }

    [Fact]
    public void Compress_CollapsesTripleNewlinesToDouble()
    {
        var c = NewCompressor();
        var req = NewRequest("line1\n\n\n\nline2");

        var result = c.Compress(req);
        result.Messages[0].Content.Should().Be("line1\n\nline2");
    }

    [Fact]
    public void Compress_TrimsTrailingWhitespacePerLine()
    {
        var c = NewCompressor();
        var req = NewRequest("line1   \nline2\t\t\nline3");

        var result = c.Compress(req);
        result.Messages[0].Content.Should().Be("line1\nline2\nline3");
    }

    [Fact]
    public void Compress_ShortMessages_NotSlimmed()
    {
        var c = NewCompressor(o => o.PerMessageCharBudget = 100);
        var req = NewRequest("a short message");

        var result = c.Compress(req);
        result.Messages[0].Content.Should().Be("a short message");
    }

    [Fact]
    public void Compress_LongMessage_DropsMiddle()
    {
        var c = NewCompressor(o =>
        {
            o.PerMessageCharBudget = 100;
            o.HeadTailKeepRatio = 0.2;
        });
        var longContent = new string('x', 1000);
        var req = NewRequest(longContent);

        var result = c.Compress(req);
        var content = result.Messages[0].Content!;

        // Marker text — written without "[]" so FluentAssertions'
        // string-display heuristics don't trip on it.
        content.Should().Contain("truncated by Slimmer input compressor");
        content.Length.Should().BeLessThan(longContent.Length,
            "slimming should reduce the size");
        // Head and tail kept verbatim
        content.Should().StartWith(new string('x', 200));
        content[^200..].Should().Be(new string('x', 200));
    }

    [Fact]
    public void Compress_PreservesToolCallFields()
    {
        var c = NewCompressor();
        var toolCalls = new List<ToolCall>
        {
            new() { Id = "call_1", Type = "function", Function = new ToolCallFunction { Name = "search", Arguments = "{}" } }
        };
        var req = new ChatRequest
        {
            Model = "gpt-4",
            Messages = new List<ChatMessage>
            {
                new() { Role = "assistant", Content = "calling tool", ToolCalls = toolCalls }
            },
        };

        var result = c.Compress(req);

        result.Messages[0].ToolCalls.Should().BeSameAs(toolCalls);
        result.Messages[0].Role.Should().Be("assistant");
    }

    [Fact]
    public void Compress_SameInputSameOutput_PureFunction()
    {
        var c = NewCompressor();
        var req = NewRequest("line1\n\n\nline2\t  ");

        var r1 = c.Compress(req);
        var r2 = c.Compress(req);

        r1.Messages[0].Content.Should().Be(r2.Messages[0].Content);
    }

    [Fact]
    public void Compress_DoesNotMutateInputRequest()
    {
        var c = NewCompressor();
        var original = "line1\n\n\nline2";
        var req = NewRequest(original);

        c.Compress(req);

        req.Messages[0].Content.Should().Be(original,
            "the compressor must not mutate the caller's request");
    }

    [Fact]
    public void Compress_EmptyMessages_ReturnsAsIs()
    {
        var c = NewCompressor();
        var req = new ChatRequest { Model = "gpt-4", Messages = new List<ChatMessage>() };

        var result = c.Compress(req);
        result.Messages.Should().BeEmpty();
    }

    [Fact]
    public void Compress_NullContent_HandledGracefully()
    {
        var c = NewCompressor();
        var req = new ChatRequest
        {
            Model = "gpt-4",
            Messages = new List<ChatMessage> { new() { Role = "user", Content = null! } }
        };

        var result = c.Compress(req);
        result.Messages[0].Content.Should().Be(string.Empty);
    }
}
