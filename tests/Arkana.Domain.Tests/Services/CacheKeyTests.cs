using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using FluentAssertions;

namespace Arkana.Domain.Tests.Services;

/// <summary>
/// Behavioral tests for <see cref="CacheKey"/>. The cache key is the
/// foundation of PERF-ARKANA-002 — if two semantically identical requests
/// hash differently, the cache hit rate collapses to zero. These tests
/// pin the canonicalization rules so future refactors can't quietly
/// change them.
/// </summary>
public sealed class CacheKeyTests
{
    private static ChatRequest SampleRequest() => new()
    {
        Model = "gpt-4",
        Messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = "You are a helpful assistant." },
            new() { Role = "user", Content = "Hello!" }
        }
    };

    [Fact]
    public void From_StableAcrossCalls_ForSameRequest()
    {
        var request = SampleRequest();

        var k1 = CacheKey.From(request);
        var k2 = CacheKey.From(request);

        k1.Hash.Should().Be(k2.Hash);
    }

    [Fact]
    public void From_DifferentModel_ProducesDifferentKey()
    {
        var a = CacheKey.From(SampleRequest());
        var b = CacheKey.From(new ChatRequest
        {
            Model = "gpt-3.5-turbo",
            Messages = SampleRequest().Messages
        });

        a.Hash.Should().NotBe(b.Hash);
    }

    [Fact]
    public void From_ModelNameIsCaseInsensitive()
    {
        var a = CacheKey.From(new ChatRequest
        {
            Model = "GPT-4",
            Messages = SampleRequest().Messages
        });
        var b = CacheKey.From(new ChatRequest
        {
            Model = "gpt-4",
            Messages = SampleRequest().Messages
        });

        a.Hash.Should().Be(b.Hash);
    }

    [Fact]
    public void From_DifferentMessageContent_ProducesDifferentKey()
    {
        var a = CacheKey.From(SampleRequest());
        var b = CacheKey.From(new ChatRequest
        {
            Model = "gpt-4",
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "Goodbye!" }
            }
        });

        a.Hash.Should().NotBe(b.Hash);
    }

    [Fact]
    public void From_DifferentMessageOrder_ProducesDifferentKey()
    {
        // Order matters in chat — flipping system/user changes the
        // prompt semantically. The cache should NOT collapse these.
        var a = CacheKey.From(new ChatRequest
        {
            Model = "gpt-4",
            Messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = "Be terse" },
                new() { Role = "user", Content = "Hello" }
            }
        });
        var b = CacheKey.From(new ChatRequest
        {
            Model = "gpt-4",
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "Hello" },
                new() { Role = "system", Content = "Be terse" }
            }
        });

        a.Hash.Should().NotBe(b.Hash);
    }

    [Fact]
    public void From_ToolsSortedByName_OrderIndependent()
    {
        var t1 = new ToolFunction { Name = "alpha", Description = "first" };
        var t2 = new ToolFunction { Name = "beta", Description = "second" };

        var requestA = new ChatRequest
        {
            Model = "gpt-4",
            Messages = SampleRequest().Messages,
            Tools = new List<ToolDefinition>
            {
                new() { Type = "function", Function = t1 },
                new() { Type = "function", Function = t2 }
            }
        };
        var requestB = new ChatRequest
        {
            Model = "gpt-4",
            Messages = SampleRequest().Messages,
            Tools = new List<ToolDefinition>
            {
                new() { Type = "function", Function = t2 },
                new() { Type = "function", Function = t1 }
            }
        };

        var kA = CacheKey.From(requestA);
        var kB = CacheKey.From(requestB);

        kA.Hash.Should().Be(kB.Hash,
            "tool ordering is canonicalized by name so semantically identical tool sets hash identically");
    }

    [Fact]
    public void From_DifferentToolName_ProducesDifferentKey()
    {
        var requestA = new ChatRequest
        {
            Model = "gpt-4",
            Messages = SampleRequest().Messages,
            Tools = new List<ToolDefinition>
            {
                new() { Function = new ToolFunction { Name = "alpha" } }
            }
        };
        var requestB = new ChatRequest
        {
            Model = "gpt-4",
            Messages = SampleRequest().Messages,
            Tools = new List<ToolDefinition>
            {
                new() { Function = new ToolFunction { Name = "beta" } }
            }
        };

        CacheKey.From(requestA).Hash.Should().NotBe(CacheKey.From(requestB).Hash);
    }

    [Fact]
    public void ToString_IncludesShortHash()
    {
        var key = CacheKey.From(SampleRequest());
        key.ToString().Should().StartWith("cache:");
        key.ToString().Length.Should().Be("cache:".Length + 16,
            "ToString() exposes a 16-char prefix for log readability");
    }

    [Fact]
    public void Equality_BasedOnHash()
    {
        var a = CacheKey.From(SampleRequest());
        var b = CacheKey.From(SampleRequest());

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }
}
