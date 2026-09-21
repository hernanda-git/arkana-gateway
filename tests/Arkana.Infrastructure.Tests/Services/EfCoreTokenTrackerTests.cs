using Arkana.Domain.Interfaces;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Entities;
using Arkana.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.Services;

public sealed class EfCoreTokenTrackerTests
{
    private static GatewayDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new GatewayDbContext(options);
    }

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
    public async Task RecordUsageAsync_Should_PersistUsage()
    {
        // Arrange
        using var db = CreateDbContext();
        var tracker = new EfCoreTokenTracker(db);
        var usage = CreateUsage();

        // Act
        await tracker.RecordUsageAsync(usage, CancellationToken.None);

        // Assert
        var recent = await tracker.GetRecentUsageAsync(10, CancellationToken.None);
        recent.Should().ContainSingle();
        recent[0].Provider.Should().Be(usage.Provider);
        recent[0].Model.Should().Be(usage.Model);
        recent[0].InputTokens.Should().Be(usage.InputTokens);
        recent[0].OutputTokens.Should().Be(usage.OutputTokens);
        recent[0].Cost.Should().Be(usage.Cost);
        recent[0].ApiKeyName.Should().Be(usage.ApiKeyName);
    }

    [Fact]
    public async Task GetUsageAsync_Should_ReturnUsagesWithinTimeRange()
    {
        // Arrange
        using var db = CreateDbContext();
        var tracker = new EfCoreTokenTracker(db);
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-10);
        var target = now.AddDays(-1);
        var future = now.AddDays(1);

        await tracker.RecordUsageAsync(
            CreateUsage(timestamp: old), CancellationToken.None);
        await tracker.RecordUsageAsync(
            CreateUsage(timestamp: target), CancellationToken.None);
        await tracker.RecordUsageAsync(
            CreateUsage(timestamp: future), CancellationToken.None);

        // Act
        var result = await tracker.GetUsageAsync(
            now.AddDays(-2), now.AddDays(2), CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result.Should().BeInDescendingOrder(u => u.Timestamp);
    }

    [Fact]
    public async Task GetUsageAsync_Should_ReturnEmpty_WhenNoUsagesInRange()
    {
        // Arrange
        using var db = CreateDbContext();
        var tracker = new EfCoreTokenTracker(db);

        await tracker.RecordUsageAsync(
            CreateUsage(timestamp: DateTimeOffset.UtcNow), CancellationToken.None);

        // Act
        var result = await tracker.GetUsageAsync(
            DateTimeOffset.UtcNow.AddDays(10), DateTimeOffset.UtcNow.AddDays(20),
            CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTotalCostAsync_Should_SumCostWithinRange()
    {
        // Arrange
        using var db = CreateDbContext();
        var tracker = new EfCoreTokenTracker(db);
        var now = DateTimeOffset.UtcNow;

        await tracker.RecordUsageAsync(
            CreateUsage(cost: 1.5m, timestamp: now.AddHours(-2)), CancellationToken.None);
        await tracker.RecordUsageAsync(
            CreateUsage(cost: 2.5m, timestamp: now.AddHours(-1)), CancellationToken.None);
        await tracker.RecordUsageAsync(
            CreateUsage(cost: 3.5m, timestamp: now.AddDays(1)), CancellationToken.None);

        // Act
        var total = await tracker.GetTotalCostAsync(
            now.AddDays(-1), now.AddMinutes(1), CancellationToken.None);

        // Assert
        total.Should().Be(4.0m);
    }

    [Fact]
    public async Task GetTotalCostAsync_Should_ReturnZero_WhenNoUsages()
    {
        // Arrange
        using var db = CreateDbContext();
        var tracker = new EfCoreTokenTracker(db);

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
        using var db = CreateDbContext();
        var tracker = new EfCoreTokenTracker(db);
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
        using var db = CreateDbContext();
        var tracker = new EfCoreTokenTracker(db);
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
        using var db = CreateDbContext();
        var tracker = new EfCoreTokenTracker(db);

        // Act
        var result = await tracker.GetRecentUsageAsync(10, CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task MultipleTrackers_Should_BeIsolatedByDbContext()
    {
        // Arrange
        using var db1 = CreateDbContext();
        using var db2 = CreateDbContext();
        var tracker1 = new EfCoreTokenTracker(db1);
        var tracker2 = new EfCoreTokenTracker(db2);

        // Act
        await tracker1.RecordUsageAsync(
            CreateUsage(provider: "ProviderA"), CancellationToken.None);
        await tracker2.RecordUsageAsync(
            CreateUsage(provider: "ProviderB"), CancellationToken.None);

        // Assert
        var result1 = await tracker1.GetRecentUsageAsync(10, CancellationToken.None);
        var result2 = await tracker2.GetRecentUsageAsync(10, CancellationToken.None);

        result1.Should().ContainSingle().Which.Provider.Should().Be("ProviderA");
        result2.Should().ContainSingle().Which.Provider.Should().Be("ProviderB");
    }

    [Fact]
    public async Task TenantContext_OnlyReadsUsageForCurrentTenant()
    {
        using var db = CreateDbContext();
        var currentTenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(currentTenant);
        var tracker = new EfCoreTokenTracker(db, tenant);
        var now = DateTimeOffset.UtcNow;

        db.TokenUsages.Add(new TokenUsageEntity
        {
            Id = Guid.NewGuid(),
            Provider = "Other",
            Model = "other-model",
            InputTokens = 900,
            OutputTokens = 100,
            Cost = 1m,
            DurationTicks = TimeSpan.FromSeconds(1).Ticks,
            Timestamp = now,
            ApiKeyName = "same-name",
            TenantId = otherTenant
        });
        await db.SaveChangesAsync();
        await tracker.RecordUsageAsync(CreateUsage(timestamp: now) with { ApiKeyName = "same-name" });

        var result = await tracker.GetUsageAsync(now.AddMinutes(-1), now.AddMinutes(1));

        result.Should().ContainSingle();
        result[0].TenantId.Should().Be(currentTenant);
        result[0].Provider.Should().Be("OpenAI");
    }
}
