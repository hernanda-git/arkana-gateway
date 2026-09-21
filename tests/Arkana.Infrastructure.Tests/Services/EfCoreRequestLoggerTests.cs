using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.Services;

public sealed class EfCoreRequestLoggerTests
{
    private sealed class TestTenantProvider(Guid? tenantId) : ITenantProvider
    {
        public Guid? TenantId { get; } = tenantId;
    }

    private static EfCoreRequestLogger CreateLogger(GatewayDbContext db, Guid tenantId = default)
        => new(db, new TestTenantProvider(tenantId), Substitute.For<IProviderAccountRepository>());

    private static GatewayDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new GatewayDbContext(options);
    }

    private static RequestLog CreateLog(string provider = "OpenAI",
        string model = "gpt-4",
        string? apiKeyName = "test-key",
        int inputTokens = 100,
        int outputTokens = 50,
        decimal cost = 0.0025m,
        TimeSpan? duration = null,
        string? responseContent = "Hello, world!",
        List<ChatMessage>? messages = null,
        List<ToolCallInfo>? toolCalls = null,
        bool isError = false,
        string? errorMessage = null,
        DateTimeOffset? timestamp = null,
        Guid? id = null,
        Guid? tenantId = null,
        Guid? resolvedProviderAccountId = null,
        string? resolvedProviderAccountCode = null,
        string routeKind = "unknown")
    {
        var log = new RequestLog
        {
            Id = id ?? Guid.NewGuid(),
            Provider = provider,
            Model = model,
            ApiKeyName = apiKeyName,
            TenantId = tenantId ?? Guid.Empty,
            ResolvedProviderAccountId = resolvedProviderAccountId,
            ResolvedProviderAccountCode = resolvedProviderAccountCode,
            RouteKind = routeKind,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Cost = cost,
            Duration = duration ?? TimeSpan.FromSeconds(1),
            ResponseContent = responseContent,
            Messages = messages ?? [new ChatMessage { Role = "user", Content = "Hello" }],
            ToolCalls = toolCalls,
            IsError = isError,
            ErrorMessage = errorMessage,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow
        };
        return log;
    }

    [Fact]
    public async Task RecordAsync_Should_RejectMissingTenant()
    {
        using var db = CreateDbContext();
        var logger = new EfCoreRequestLogger(db, new TestTenantProvider(null), Substitute.For<IProviderAccountRepository>());

        var act = () => logger.RecordAsync(CreateLog(tenantId: Guid.NewGuid()));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RecordAsync_Should_RejectTenantMismatch()
    {
        using var db = CreateDbContext();
        var logger = CreateLogger(db, Guid.NewGuid());

        var act = () => logger.RecordAsync(CreateLog(tenantId: Guid.NewGuid()));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RecordAsync_Should_RoundTripDurableAttribution()
    {
        using var db = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var accounts = Substitute.For<IProviderAccountRepository>();
        accounts.GetByIdAsync(tenantId, accountId, Arg.Any<CancellationToken>())
            .Returns(ProviderAccount.Create(tenantId, Guid.NewGuid(), "gemini-acc2", "Gemini 2"));
        var logger = new EfCoreRequestLogger(db, new TestTenantProvider(tenantId), accounts);
        var log = CreateLog(tenantId: tenantId, resolvedProviderAccountId: accountId,
            resolvedProviderAccountCode: "gemini-acc2", routeKind: "broker-managed");

        await logger.RecordAsync(log);
        var retrieved = await logger.GetByIdAsync(log.Id);

        retrieved.Should().NotBeNull();
        retrieved!.TenantId.Should().Be(tenantId);
        retrieved.ResolvedProviderAccountId.Should().Be(accountId);
        retrieved.ResolvedProviderAccountCode.Should().Be("gemini-acc2");
        retrieved.RouteKind.Should().Be("broker-managed");
    }

    [Fact]
    public async Task RecordAsync_Should_PersistLog()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);
        var log = CreateLog();

        // Act
        await logger.RecordAsync(log, CancellationToken.None);

        // Assert
        var retrieved = await logger.GetByIdAsync(log.Id, CancellationToken.None);
        retrieved.Should().NotBeNull();
        retrieved!.Provider.Should().Be(log.Provider);
        retrieved.Model.Should().Be(log.Model);
        retrieved.ApiKeyName.Should().Be(log.ApiKeyName);
        retrieved.InputTokens.Should().Be(log.InputTokens);
        retrieved.OutputTokens.Should().Be(log.OutputTokens);
        retrieved.Cost.Should().Be(log.Cost);
        retrieved.ResponseContent.Should().Be(log.ResponseContent);
        retrieved.IsError.Should().Be(log.IsError);
    }

    [Fact]
    public async Task RecordAsync_Should_PersistWithToolCalls()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);
        var toolCalls = new List<ToolCallInfo>
        {
            new() { Id = "call1", Type = "function", FunctionName = "get_weather",
                     FunctionArguments = "{\"location\":\"NYC\"}" }
        };
        var log = CreateLog(toolCalls: toolCalls);

        // Act
        await logger.RecordAsync(log, CancellationToken.None);

        // Assert
        var retrieved = await logger.GetByIdAsync(log.Id, CancellationToken.None);
        retrieved.Should().NotBeNull();
        retrieved!.ToolCalls.Should().HaveCount(1);
        retrieved.ToolCalls![0].FunctionName.Should().Be("get_weather");
    }

    [Fact]
    public async Task RecordAsync_Should_PersistErrorLog()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);
        var log = CreateLog(isError: true, errorMessage: "Timeout exceeded",
            responseContent: null);

        // Act
        await logger.RecordAsync(log, CancellationToken.None);

        // Assert
        var retrieved = await logger.GetByIdAsync(log.Id, CancellationToken.None);
        retrieved.Should().NotBeNull();
        retrieved!.IsError.Should().BeTrue();
        retrieved.ErrorMessage.Should().Be("Timeout exceeded");
        retrieved.ResponseContent.Should().BeNull();
    }

    [Fact]
    public async Task GetRecentAsync_Should_ReturnMostRecentLogs()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 10; i++)
        {
            await logger.RecordAsync(
                CreateLog(timestamp: now.AddMinutes(-i)), CancellationToken.None);
        }

        // Act
        var result = await logger.GetRecentAsync(3, CancellationToken.None);

        // Assert
        result.Should().HaveCount(3);
        result.Should().BeInDescendingOrder(l => l.Timestamp);
    }

    [Fact]
    public async Task GetRecentAsync_Should_ReturnAll_WhenCountExceedsTotal()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);

        await logger.RecordAsync(CreateLog(), CancellationToken.None);
        await logger.RecordAsync(CreateLog(), CancellationToken.None);

        // Act
        var result = await logger.GetRecentAsync(100, CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRecentAsync_Should_ReturnEmpty_WhenNoLogs()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);

        // Act
        var result = await logger.GetRecentAsync(10, CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecentAsync_Should_UseDefaultCountOf100()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);

        for (int i = 0; i < 150; i++)
        {
            await logger.RecordAsync(
                CreateLog(timestamp: DateTimeOffset.UtcNow.AddMinutes(-i)),
                CancellationToken.None);
        }

        // Act
        var result = await logger.GetRecentAsync(count: 100, ct: CancellationToken.None);

        // Assert
        result.Should().HaveCount(100);
    }

    [Fact]
    public async Task GetByIdAsync_Should_ReturnNull_WhenNotFound()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);

        // Act
        var result = await logger.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_Should_FilterByProvider()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);

        await logger.RecordAsync(CreateLog(provider: "OpenAI"), CancellationToken.None);
        await logger.RecordAsync(CreateLog(provider: "Gemini"), CancellationToken.None);
        await logger.RecordAsync(CreateLog(provider: "OpenAI"), CancellationToken.None);

        // Act
        var result = await logger.SearchAsync(providerFilter: "OpenAI",
            ct: CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result.Should().AllSatisfy(l => l.Provider.Should().Be("OpenAI"));
    }

    [Fact]
    public async Task SearchAsync_Should_FilterByModel()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);

        await logger.RecordAsync(CreateLog(model: "gpt-4"), CancellationToken.None);
        await logger.RecordAsync(CreateLog(model: "gemini-pro"), CancellationToken.None);

        // Act
        var result = await logger.SearchAsync(modelFilter: "gpt-4",
            ct: CancellationToken.None);

        // Assert
        result.Should().ContainSingle().Which.Model.Should().Be("gpt-4");
    }

    [Fact]
    public async Task SearchAsync_Should_FilterByApiKey()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);

        await logger.RecordAsync(CreateLog(apiKeyName: "key-a"), CancellationToken.None);
        await logger.RecordAsync(CreateLog(apiKeyName: "key-b"), CancellationToken.None);

        // Act
        var result = await logger.SearchAsync(apiKeyFilter: "key-a",
            ct: CancellationToken.None);

        // Assert
        result.Should().ContainSingle().Which.ApiKeyName.Should().Be("key-a");
    }

    [Fact]
    public async Task SearchAsync_Should_FilterByDateRange()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);
        var now = DateTimeOffset.UtcNow;

        await logger.RecordAsync(
            CreateLog(timestamp: now.AddDays(-5)), CancellationToken.None);
        await logger.RecordAsync(
            CreateLog(timestamp: now.AddDays(-1)), CancellationToken.None);
        await logger.RecordAsync(
            CreateLog(timestamp: now.AddDays(1)), CancellationToken.None);

        // Act
        var result = await logger.SearchAsync(
            from: now.AddDays(-3), to: now.AddDays(2),
            ct: CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAsync_Should_ReturnAll_WhenNoFilters()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);

        await logger.RecordAsync(CreateLog(), CancellationToken.None);
        await logger.RecordAsync(CreateLog(), CancellationToken.None);
        await logger.RecordAsync(CreateLog(), CancellationToken.None);

        // Act
        var result = await logger.SearchAsync(ct: CancellationToken.None);

        // Assert
        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task SearchAsync_Should_RespectMaxResults()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);

        for (int i = 0; i < 20; i++)
        {
            await logger.RecordAsync(
                CreateLog(timestamp: DateTimeOffset.UtcNow.AddMinutes(-i)),
                CancellationToken.None);
        }

        // Act
        var result = await logger.SearchAsync(maxResults: 5, ct: CancellationToken.None);

        // Assert
        result.Should().HaveCount(5);
    }

    [Fact]
    public async Task SearchAsync_Should_SkipWildcardFilters()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);

        await logger.RecordAsync(CreateLog(provider: "OpenAI"), CancellationToken.None);
        await logger.RecordAsync(CreateLog(provider: "Gemini"), CancellationToken.None);

        // Act — "*" should be treated as "no filter"
        var result = await logger.SearchAsync(providerFilter: "*",
            ct: CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAsync_Should_ReturnEmpty_WhenNoLogs()
    {
        // Arrange
        using var db = CreateDbContext();
        var logger = CreateLogger(db);

        // Act
        var result = await logger.SearchAsync(ct: CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }
}
