using Arkana.Domain.Services;
using Arkana.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Arkana.Infrastructure.Tests.Services;

/// <summary>
/// Tests for the Terse output compressor (AI-ARKANA-003).
/// Verifies the disabled-by-default no-op behavior, filler
/// stripping, whitespace collapse, and the SavedCharacters
/// accounting.
/// </summary>
public sealed class TerseOutputCompressorTests
{
    private static TerseOutputCompressor NewCompressor(
        Action<CompressionOptions>? configure = null)
    {
        var opts = new CompressionOptions { OutputEnabled = true };
        configure?.Invoke(opts);
        return new TerseOutputCompressor(
            Options.Create(opts),
            NullLogger<TerseOutputCompressor>.Instance);
    }

    [Fact]
    public void Compress_WhenDisabled_ReturnsContentUnchanged()
    {
        var c = NewCompressor(o => o.OutputEnabled = false);

        var result = c.Compress("Sure, here is the answer.");
        result.Should().Be("Sure, here is the answer.");
    }

    [Fact]
    public void Compress_StripsSurePrefix()
    {
        var c = NewCompressor();
        var result = c.Compress("Sure, the answer is 42.");
        result.Should().Be("the answer is 42.");
    }

    [Fact]
    public void Compress_StripsHereIsPrefix()
    {
        var c = NewCompressor();
        var result = c.Compress("Here is the answer: 42.");
        result.Should().Be("answer: 42.");
    }

    [Fact]
    public void Compress_StripsLetMeAssistPrefix()
    {
        var c = NewCompressor();
        var result = c.Compress("Let me help you with that, the result is 42.");
        result.Should().Be("the result is 42.");
    }

    [Fact]
    public void Compress_StripsCertainlyPrefix()
    {
        var c = NewCompressor();
        var result = c.Compress("Certainly! The answer is 42.");
        result.Should().StartWith("The answer");
    }

    [Fact]
    public void Compress_CollapsesExcessBlankLines()
    {
        var c = NewCompressor();
        var result = c.Compress("line1\n\n\n\n\nline2");
        result.Should().Be("line1\n\nline2");
    }

    [Fact]
    public void Compress_TrimsTrailingWhitespacePerLine()
    {
        var c = NewCompressor();
        var result = c.Compress("line1   \nline2\t");
        // Trailing \t on the final line is trimmed (Multiline $).
        // The remaining content is "line1\nline2" — verify it
        // doesn't END with whitespace.
        result.TrimEnd().Should().Be(result);
        // Also: the final char should be '2', not whitespace.
        result.Should().EndWith("line2");
    }

    [Fact]
    public void Compress_PreservesContentInMiddleOfResponse()
    {
        var c = NewCompressor();
        // Filler anchored at start only — must not be stripped from
        // the middle of a quoted passage.
        var result = c.Compress("The model said: \"Sure, I'll do that\" and left.");
        result.Should().Contain("Sure, I'll do that");
    }

    [Fact]
    public void Compress_AccumulatesSavedCharacters()
    {
        var c = NewCompressor();
        c.Compress("Sure, hello there.");
        c.Compress("Here is the answer.");
        c.SavedCharacters.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Compress_NoChangeForCleanContent()
    {
        var c = NewCompressor();
        var input = "Just a direct answer with no filler.";
        var result = c.Compress(input);
        result.Should().Be(input);
    }
}
