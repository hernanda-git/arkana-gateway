using Arkana.Application.Features.Chat.Handlers;
using Arkana.Application.Features.Chat.Queries;
using Arkana.Domain.Interfaces;
using Arkana.Domain.ValueObjects;
using FluentAssertions;
using NSubstitute;

namespace Arkana.Application.Tests.Features.Chat.Handlers;

public class GetTokenUsageHandlerTests
{
    private readonly ITokenTracker _tokenTracker = Substitute.For<ITokenTracker>();
    private readonly GetTokenUsageHandler _sut;

    public GetTokenUsageHandlerTests()
    {
        _sut = new GetTokenUsageHandler(_tokenTracker);
    }

    [Fact]
    public async Task Handle_ShouldReturnSummaryWithCorrectTotals()
    {
        // Arrange
        var from = DateTimeOffset.UtcNow.AddDays(-7);
        var to = DateTimeOffset.UtcNow;

        var usage = new List<TokenUsage>
        {
            new("OpenAI", "gpt-4", 100, 50, 0.01m, TimeSpan.FromMilliseconds(500)),
            new("OpenAI", "gpt-4", 200, 100, 0.02m, TimeSpan.FromMilliseconds(800)),
            new("Anthropic", "claude-3", 150, 75, 0.015m, TimeSpan.FromMilliseconds(600)),
            new("Anthropic", "claude-3", 50, 25, 0.005m, TimeSpan.FromMilliseconds(300)),
        };

        _tokenTracker.GetUsageAsync(from, to, Arg.Any<CancellationToken>())
            .Returns(usage);

        _tokenTracker.GetTotalCostAsync(from, to, Arg.Any<CancellationToken>())
            .Returns(0.05m);

        var query = new GetTokenUsageQuery
        {
            From = from,
            To = to
        };

        // Act
        var result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.TotalRequests.Should().Be(4);
        result.TotalTokens.Should().Be(100 + 200 + 150 + 50 + 50 + 100 + 75 + 25); // Input + Output for all
        result.TotalCost.Should().Be(0.05m);
    }

    [Fact]
    public async Task Handle_ShouldGroupResultsByProvider()
    {
        // Arrange
        var from = DateTimeOffset.UtcNow.AddDays(-7);
        var to = DateTimeOffset.UtcNow;

        var usage = new List<TokenUsage>
        {
            new("OpenAI", "gpt-4", 100, 50, 0.01m, TimeSpan.FromMilliseconds(500)),
            new("OpenAI", "gpt-4", 200, 100, 0.02m, TimeSpan.FromMilliseconds(800)),
            new("Anthropic", "claude-3", 150, 75, 0.015m, TimeSpan.FromMilliseconds(600)),
        };

        _tokenTracker.GetUsageAsync(from, to, Arg.Any<CancellationToken>())
            .Returns(usage);

        _tokenTracker.GetTotalCostAsync(from, to, Arg.Any<CancellationToken>())
            .Returns(0.045m);

        var query = new GetTokenUsageQuery
        {
            From = from,
            To = to
        };

        // Act
        var result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.ByProvider.Should().HaveCount(2);

        var openAi = result.ByProvider.Single(p => p.Provider == "OpenAI");
        openAi.Requests.Should().Be(2);
        openAi.Tokens.Should().Be(100 + 50 + 200 + 100); // InputTokens + OutputTokens for both OpenAI records
        openAi.Cost.Should().Be(0.03m);

        var anthropic = result.ByProvider.Single(p => p.Provider == "Anthropic");
        anthropic.Requests.Should().Be(1);
        anthropic.Tokens.Should().Be(150 + 75); // InputTokens + OutputTokens
        anthropic.Cost.Should().Be(0.015m);
    }
}
