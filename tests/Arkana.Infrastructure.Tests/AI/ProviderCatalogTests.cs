using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.AI;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.AI;

/// <summary>
/// Unit tests for <see cref="ProviderCatalog"/> — the in-memory snapshot
/// that absorbs the N+1 provider-table reads on the chat hot path
/// (PERF-ARKANA-001).
/// </summary>
public class ProviderCatalogTests
{
    private static AiProvider Sample(string code, int priority, bool enabled = true)
    {
        var p = AiProvider.Create($"P-{code}", code, priority);
        if (!enabled) p.Disable();
        return p;
    }

    // Use a fake TimeProvider so TTL behavior is deterministic.
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 19, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (ProviderCatalog Sut, IAiProviderRepository Repo, FakeTime Time) BuildSut(
        TimeSpan? ttl = null,
        params AiProvider[] seed)
    {
        var repo = Substitute.For<IAiProviderRepository>();
        repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(seed.ToList());
        var time = new FakeTime();

        // ProviderCatalog is a singleton that resolves the scoped repository
        // from a fresh DI scope on each refresh. Build a scope factory that
        // hands back the mock repository so the unit tests stay hermetic.
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.GetService(typeof(IAiProviderRepository)).Returns(repo);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateAsyncScope().Returns(scope);

        var catalog = new ProviderCatalog(
            scopeFactory,
            NullLogger<ProviderCatalog>.Instance,
            time,
            ttl);
        return (catalog, repo, time);
    }

    // Builds a ProviderCatalog that resolves the given (mock) repository from a
    // fresh DI scope on each refresh — mirrors the production singleton behavior.
    private static ProviderCatalog BuildFromRepo(
        IAiProviderRepository repo,
        FakeTime time,
        TimeSpan? ttl = null)
    {
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.GetService(typeof(IAiProviderRepository)).Returns(repo);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateAsyncScope().Returns(scope);
        return new ProviderCatalog(
            scopeFactory,
            NullLogger<ProviderCatalog>.Instance,
            time,
            ttl);
    }

    [Fact]
    public async Task GetAllAsync_ColdCache_LoadsFromRepository()
    {
        var (sut, repo, _) = BuildSut(ttl: TimeSpan.FromSeconds(30),
            Sample("opencode", 0), Sample("deepseek", 1));

        var result = await sut.GetAllAsync();

        result.Should().HaveCount(2);
        result.Select(p => p.Code).Should().BeEquivalentTo("opencode", "deepseek");
        await repo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAllAsync_WithinTtl_DoesNotRereadRepository()
    {
        var (sut, repo, time) = BuildSut(ttl: TimeSpan.FromSeconds(30),
            Sample("opencode", 0));
        await sut.GetAllAsync();
        time.Now = time.Now.AddSeconds(20); // still fresh
        await sut.GetAllAsync();
        await sut.GetAllAsync();

        await repo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAllAsync_AfterTtl_ReloadsFromRepository()
    {
        var (sut, repo, time) = BuildSut(ttl: TimeSpan.FromSeconds(30),
            Sample("opencode", 0));
        await sut.GetAllAsync();
        time.Now = time.Now.AddSeconds(31);
        repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AiProvider> { Sample("opencode", 0), Sample("deepseek", 1) });
        await sut.GetAllAsync();

        await repo.Received(2).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalidate_ForcesImmediateReload()
    {
        var (sut, repo, _) = BuildSut(ttl: TimeSpan.FromSeconds(30),
            Sample("opencode", 0));
        await sut.GetAllAsync();
        sut.Invalidate();
        await sut.GetAllAsync();

        await repo.Received(2).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByCodeAsync_UnknownCode_ReturnsNull()
    {
        var (sut, _, _) = BuildSut(ttl: TimeSpan.FromSeconds(30),
            Sample("opencode", 0));

        var result = await sut.GetByCodeAsync("does-not-exist");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByCodeAsync_KnownCode_ReturnsProvider()
    {
        var (sut, _, _) = BuildSut(ttl: TimeSpan.FromSeconds(30),
            Sample("opencode", 0), Sample("deepseek", 1));

        var result = await sut.GetByCodeAsync("DEEPSEEK"); // case-insensitive

        result.Should().NotBeNull();
        result!.Code.Should().Be("deepseek");
    }

    [Fact]
    public async Task GetAllEnabledAsync_FiltersDisabledAndOrdersByPriority()
    {
        var (sut, _, _) = BuildSut(ttl: TimeSpan.FromSeconds(30),
            Sample("opencode", 0),
            Sample("deepseek", 2, enabled: false),
            Sample("anthropic", 1));

        var enabled = await sut.GetAllEnabledAsync();

        enabled.Select(p => p.Code).Should().Equal("opencode", "anthropic");
    }

    [Fact]
    public async Task Snapshot_ReturnsDefensiveCopy()
    {
        var (sut, _, _) = BuildSut(ttl: TimeSpan.FromSeconds(30),
            Sample("opencode", 0));
        var first = (List<AiProvider>)await sut.GetAllAsync();
        first.Should().HaveCount(1);

        // Mutating the returned list must not affect the catalog's snapshot.
        first.Clear();
        var second = await sut.GetAllAsync();
        second.Should().HaveCount(1);
    }

    [Fact]
    public async Task Concurrent_Access_TriggersSingleRefresh()
    {
        // The single-flight gate must coalesce N concurrent stale-cache
        // reads into exactly one repository call.
        var repo = Substitute.For<IAiProviderRepository>();
        repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new List<AiProvider> { Sample("opencode", 0) });

        var time = new FakeTime();
        var sut = BuildFromRepo(repo, time, ttl: TimeSpan.FromSeconds(30));

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => sut.GetAllAsync())
            .ToArray();
        await Task.WhenAll(tasks);

        await repo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }
}
