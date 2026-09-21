using Arkana.Domain.ValueObjects;
using FluentAssertions;

namespace Arkana.Domain.Tests.ValueObjects;

public sealed class TokenUsageTests
{
    [Fact]
    public void Constructor_SetsAllPropertiesCorrectly()
    {
        var duration = TimeSpan.FromMilliseconds(500);
        var usage = new TokenUsage("OpenAI", "gpt-4o", 100, 50, 0.002m, duration, "test-key");

        usage.Provider.Should().Be("OpenAI");
        usage.Model.Should().Be("gpt-4o");
        usage.InputTokens.Should().Be(100);
        usage.OutputTokens.Should().Be(50);
        usage.Cost.Should().Be(0.002m);
        usage.Duration.Should().Be(duration);
        usage.ApiKeyName.Should().Be("test-key");
        usage.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void TotalTokens_EqualsInputTokensPlusOutputTokens()
    {
        var usage = new TokenUsage("OpenAI", "gpt-4o", 100, 50, 0.002m, TimeSpan.Zero);

        usage.TotalTokens.Should().Be(150);
    }
}
