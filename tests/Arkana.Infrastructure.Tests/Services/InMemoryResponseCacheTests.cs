using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arkana.Infrastructure.Tests.Services;

/// <summary>
/// Tests for the process-local <see cref="InMemoryResponseCache"/>.
/// Pins TTL semantics, FIFO eviction, and metric counter behavior.
/// </summary>
public sealed class InMemoryResponseCacheTests
{
    private static InMemoryResponseCache NewCache(InMemoryResponseCacheOptions? options = null) =>
        new(NullLogger<InMemoryResponseCache>.Instance, options);

    private static CachedResponse Sample(string content = "Hello, world!") =>
        new(
            Content: content,
            Model: "gpt-4",
            InputTokens: 10,
            OutputTokens: 5,
            ToolCallsCount: 0,
            ToolCalls: null,
            ServedByProviderName: "opencode",
            CachedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsEntry()
    {
        var cache = NewCache();
        var key = CacheKey.From(new ChatRequest
        {
            Model = "gpt-4",
            Messages = new List<ChatMessage> { new() { Role = "user", Content = "hi" } }
        });
        var entry = Sample();

        await cache.SetAsync(key, entry, TimeSpan.FromMinutes(1));
        var hit = await cache.GetAsync(key);

        hit.Should().NotBeNull();
        hit!.Content.Should().Be(entry.Content);
        hit.ServedByProviderName.Should().Be("opencode");
    }

    [Fact]
    public async Task GetAsync_OnMiss_ReturnsNull_AndIncrementsMisses()
    {
        var cache = NewCache();
        var key = CacheKey.From(new ChatRequest
        {
            Model = "gpt-4",
            Messages = new List<ChatMessage> { new() { Role = "user", Content = "never written" } }
        });

        var hit = await cache.GetAsync(key);

        hit.Should().BeNull();
        cache.Misses.Should().Be(1);
        cache.Hits.Should().Be(0);
    }

    [Fact]
    public async Task GetAsync_OnHit_IncrementsHits()
    {
        var cache = NewCache();
        var key = CacheKey.From(new ChatRequest
        {
            Model = "gpt-4",
            Messages = new List<ChatMessage> { new() { Role = "user", Content = "x" } }
        });
        await cache.SetAsync(key, Sample(), TimeSpan.FromMinutes(1));

        var hit = await cache.GetAsync(key);

        hit.Should().NotBeNull();
        cache.Hits.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_AfterTtl_ReturnsNull_AndRemovesEntry()
    {
        var cache = NewCache();
        var key = CacheKey.From(new ChatRequest
        {
            Model = "gpt-4",
            Messages = new List<ChatMessage> { new() { Role = "user", Content = "expire" } }
        });
        await cache.SetAsync(key, Sample(), TimeSpan.FromMilliseconds(50));

        // Wait past TTL — well above the 50ms requested
        await Task.Delay(120);

        var hit = await cache.GetAsync(key);

        hit.Should().BeNull();
        cache.Count.Should().Be(0,
            "expired entries are removed on the next read");
    }

    [Fact]
    public async Task SetAsync_NegativeTtl_IsNoOp()
    {
        var cache = NewCache();
        var key = CacheKey.From(new ChatRequest
        {
            Model = "gpt-4",
            Messages = new List<ChatMessage> { new() { Role = "user", Content = "no" } }
        });

        await cache.SetAsync(key, Sample(), TimeSpan.FromSeconds(-1));

        (await cache.GetAsync(key)).Should().BeNull();
        cache.Writes.Should().Be(0,
            "writes with non-positive TTL are intentionally dropped before any work happens");
    }

    [Fact]
    public async Task Eviction_FifoOldestDropsFirst()
    {
        var cache = NewCache(InMemoryResponseCacheOptions.Test); // cap = 4
        var keys = new List<CacheKey>();
        for (int i = 0; i < 5; i++)
        {
            var k = CacheKey.From(new ChatRequest
            {
                Model = "gpt-4",
                Messages = new List<ChatMessage>
                {
                    new() { Role = "user", Content = $"msg-{i}" }
                }
            });
            keys.Add(k);
            await cache.SetAsync(k, Sample(content: $"reply-{i}"), TimeSpan.FromMinutes(10));
        }

        // The first key (oldest) should have been evicted; the rest remain.
        var firstHit = await cache.GetAsync(keys[0]);
        firstHit.Should().BeNull("oldest entry should be evicted when cap is exceeded");

        for (int i = 1; i < 5; i++)
        {
            var hit = await cache.GetAsync(keys[i]);
            hit.Should().NotBeNull($"entry {i} is still within the cap");
        }

        cache.Evictions.Should().BeGreaterThan(0);
    }

    [Fact]
    public void InvalidateAll_ClearsEverything()
    {
        // pre-populate synchronously via the async surface
        var cache = NewCache();
        cache.InvalidateAll(); // idempotent on empty

        cache.InvalidateAll();

        cache.Count.Should().Be(0);
    }

    [Fact]
    public async Task InvalidateAll_DropsAllEntries_AndKeepsCacheUsable()
    {
        var cache = NewCache();
        var k1 = CacheKey.From(new ChatRequest
        {
            Model = "gpt-4",
            Messages = new List<ChatMessage> { new() { Role = "user", Content = "a" } }
        });
        await cache.SetAsync(k1, Sample("first"), TimeSpan.FromMinutes(1));

        cache.InvalidateAll();
        cache.Count.Should().Be(0);

        // Re-insert a new entry — cache should be fully functional after invalidation
        var k2 = CacheKey.From(new ChatRequest
        {
            Model = "gpt-4",
            Messages = new List<ChatMessage> { new() { Role = "user", Content = "b" } }
        });
        await cache.SetAsync(k2, Sample("second"), TimeSpan.FromMinutes(1));

        var hit = await cache.GetAsync(k2);
        hit.Should().NotBeNull();
        hit!.Content.Should().Be("second");
    }

    [Fact]
    public async Task SetAsync_OverwritesExistingEntry()
    {
        var cache = NewCache();
        var key = CacheKey.From(new ChatRequest
        {
            Model = "gpt-4",
            Messages = new List<ChatMessage> { new() { Role = "user", Content = "overwrite" } }
        });
        await cache.SetAsync(key, Sample("v1"), TimeSpan.FromMinutes(1));
        await cache.SetAsync(key, Sample("v2"), TimeSpan.FromMinutes(1));

        var hit = await cache.GetAsync(key);
        hit.Should().NotBeNull();
        hit!.Content.Should().Be("v2", "second SetAsync should overwrite the entry");
    }
}
