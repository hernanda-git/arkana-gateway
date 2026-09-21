using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.Services;

namespace Arkana.Infrastructure.Tests.Services;

public sealed class InMemoryTokenTrackerTests
{
    private static TokenUsage CreateUsage(string provider = "OpenAI",
        string model = "gpt-4",
        int inputTokens = 100,
        int outputTokens = 50,
        decimal cost = 0.0025m,
        TimeSpan? duration = null,
        string? apiKeyName = "test-key",
        DateTimeOffset? timestamp = null) =>
        new(provider, model, inputTokens, outputTokens, cost,
            duration ?? TimeSpan.FromSeconds(2), apiKeyName)
        {
            Timestamp = timestamp ?? DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task RecordUsageAsync_Should_AddUsage()
    {
        // Arrange
        var tracker = new InMemoryTokenTracker();
        var usage = CreateUsage();

        // Act
        await tracker.RecordUsageAsync(usage, CancellationToken.None);

        // Assert
        var recent = await tracker.GetRecentUsageAsync(10, CancellationToken.None);
        recent.Should().ContainSingle().Which.Should().Be(usage);
    }

    [Fact]
    public async Task GetUsageAsync_Should_ReturnUsagesWithinTimeRange()
    {
        // Arrange
        var tracker = new InMemoryTokenTracker();
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-10);
        var recent = now.AddDays(-1);

        await tracker.RecordUsageAsync(
            CreateUsage(timestamp: old), CancellationToken.None);
        await tracker.RecordUsageAsync(
            CreateUsage(timestamp: recent), CancellationToken.None);
        await tracker.RecordUsageAsync(
            CreateUsage(timestamp: now), CancellationToken.None);

        // Act
        var result = await tracker.GetUsageAsync(now.AddDays(-2), now.AddHours(-1),
            CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        result[0].Timestamp.Should().BeCloseTo(recent, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetUsageAsync_Should_ReturnEmpty_WhenNoUsagesInRange()
    {
        // Arrange
        var tracker = new InMemoryTokenTracker();
        var now = DateTimeOffset.UtcNow;

        await tracker.RecordUsageAsync(
            CreateUsage(timestamp: now), CancellationToken.None);

        // Act
        var result = await tracker.GetUsageAsync(
            now.AddDays(1), now.AddDays(2), CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUsageAsync_Should_ReturnEmpty_WhenNoUsagesAtAll()
    {
        // Arrange
        var tracker = new InMemoryTokenTracker();

        // Act
        var result = await tracker.GetUsageAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTotalCostAsync_Should_SumCostWithinRange()
    {
        // Arrange
        var tracker = new InMemoryTokenTracker();
        var now = DateTimeOffset.UtcNow;

        await tracker.RecordUsageAsync(
            CreateUsage(cost: 1.0m, timestamp: now.AddHours(-2)), CancellationToken.None);
        await tracker.RecordUsageAsync(
            CreateUsage(cost: 2.0m, timestamp: now.AddHours(-1)), CancellationToken.None);
        await tracker.RecordUsageAsync(
            CreateUsage(cost: 3.0m, timestamp: now), CancellationToken.None);
        await tracker.RecordUsageAsync(
            CreateUsage(cost: 4.0m, timestamp: now.AddDays(1)), CancellationToken.None);

        // Act
        var total = await tracker.GetTotalCostAsync(
            now.AddDays(-1), now.AddMinutes(1), CancellationToken.None);

        // Assert
        total.Should().Be(6.0m);
    }

    [Fact]
    public async Task GetTotalCostAsync_Should_ReturnZero_WhenNoUsagesInRange()
    {
        // Arrange
        var tracker = new InMemoryTokenTracker();
        var now = DateTimeOffset.UtcNow;

        await tracker.RecordUsageAsync(
            CreateUsage(timestamp: now), CancellationToken.None);

        // Act
        var total = await tracker.GetTotalCostAsync(
            now.AddDays(1), now.AddDays(2), CancellationToken.None);

        // Assert
        total.Should().Be(0m);
    }

    [Fact]
    public async Task GetTotalCostAsync_Should_ReturnZero_WhenNoUsages()
    {
        // Arrange
        var tracker = new InMemoryTokenTracker();

        // Act
        var total = await tracker.GetTotalCostAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);

        // Assert
        total.Should().Be(0m);
    }

    [Fact]
    public async Task GetRecentUsageAsync_Should_ReturnMostRecentUsages()
    {
        // Arrange
        var tracker = new InMemoryTokenTracker();
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 10; i++)
        {
            await tracker.RecordUsageAsync(
                CreateUsage(timestamp: now.AddMinutes(-i)), CancellationToken.None);
        }

        // Act
        var result = await tracker.GetRecentUsageAsync(3, CancellationToken.None);

        // Assert
        result.Should().HaveCount(3);
        result.Should().BeInDescendingOrder(u => u.Timestamp);
    }

    [Fact]
    public async Task GetRecentUsageAsync_Should_ReturnAll_WhenCountExceedsTotal()
    {
        // Arrange
        var tracker = new InMemoryTokenTracker();
        var now = DateTimeOffset.UtcNow;

        await tracker.RecordUsageAsync(
            CreateUsage(timestamp: now), CancellationToken.None);
        await tracker.RecordUsageAsync(
            CreateUsage(timestamp: now.AddMinutes(-1)), CancellationToken.None);

        // Act
        var result = await tracker.GetRecentUsageAsync(100, CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRecentUsageAsync_Should_ReturnEmpty_WhenNoUsages()
    {
        // Arrange
        var tracker = new InMemoryTokenTracker();

        // Act
        var result = await tracker.GetRecentUsageAsync(10, CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecentUsageAsync_Should_UseDefaultCountOf50()
    {
        // Arrange
        var tracker = new InMemoryTokenTracker();
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 60; i++)
        {
            await tracker.RecordUsageAsync(
                CreateUsage(timestamp: now.AddMinutes(-i)), CancellationToken.None);
        }

        // Act
        var result = await tracker.GetRecentUsageAsync(ct: CancellationToken.None);

        // Assert
        result.Should().HaveCount(50);
    }

    [Fact]
    public async Task RecordUsageAsync_Should_HandleMultipleRecords()
    {
        // Arrange
        var tracker = new InMemoryTokenTracker();

        // Act
        for (int i = 0; i < 100; i++)
        {
            await tracker.RecordUsageAsync(
                CreateUsage(provider: i % 2 == 0 ? "OpenAI" : "Gemini"),
                CancellationToken.None);
        }

        // Assert
        var all = await tracker.GetRecentUsageAsync(200, CancellationToken.None);
        all.Should().HaveCount(100);
    }
}
