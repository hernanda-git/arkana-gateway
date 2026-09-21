using Arkana.Domain.Entities;
using FluentAssertions;
#pragma warning disable CA1861 // Test file — inline arrays acceptable

namespace Arkana.Domain.Tests.Entities;

public sealed class ApiKeyPoolTests
{
    private static readonly Guid ProviderId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public void Create_SetsDefaults()
    {
        var pool = ApiKeyPool.Create(ProviderId, TenantId);

        pool.Id.Should().NotBe(Guid.Empty);
        pool.AiProviderId.Should().Be(ProviderId);
        pool.TenantId.Should().Be(TenantId);
        pool.ActiveIndex.Should().Be(0);
        pool.IsActive.Should().BeTrue();
        pool.TotalRequests.Should().Be(0);
        pool.TotalRateLimits.Should().Be(0);
    }

    [Fact]
    public void AdvanceIndex_WrapsAround()
    {
        var pool = ApiKeyPool.Create(ProviderId, TenantId);
        pool.Entries.Add(ApiKeyPoolEntry.Create("key-0", "k0"));
        pool.Entries.Add(ApiKeyPoolEntry.Create("key-1", "k1"));
        pool.Entries.Add(ApiKeyPoolEntry.Create("key-2", "k2"));

        pool.AdvanceIndex().Should().Be(1);
        pool.AdvanceIndex().Should().Be(2);
        pool.AdvanceIndex().Should().Be(0); // wraps
    }

    [Fact]
    public void AdvanceIndex_SkipsInactiveEntries()
    {
        var pool = ApiKeyPool.Create(ProviderId, TenantId);
        pool.Entries.Add(ApiKeyPoolEntry.Create("key-0", "k0"));
        var inactive = ApiKeyPoolEntry.Create("key-1", "k1");
        inactive.IsActive = false;
        pool.Entries.Add(inactive);
        pool.Entries.Add(ApiKeyPoolEntry.Create("key-2", "k2"));

        // Only 2 active entries, should toggle between 0 and 1 (which maps to index 2)
        var next = pool.AdvanceIndex();
        next.Should().Be(1); // second active entry
    }

    [Fact]
    public void Entry_MarkRateLimited_UpdatesState()
    {
        var entry = ApiKeyPoolEntry.Create("key", "test");
        entry.MarkRateLimited();

        entry.RateLimitedAt.Should().NotBeNull();
        entry.ConsecutiveRateLimits.Should().Be(1);
        entry.RateLimitCount.Should().Be(1);
    }

    [Fact]
    public void Entry_MarkSuccess_ResetsConsecutive()
    {
        var entry = ApiKeyPoolEntry.Create("key", "test");
        entry.MarkRateLimited();
        entry.MarkRateLimited();
        entry.MarkSuccess();

        entry.ConsecutiveRateLimits.Should().Be(0);
        entry.RequestCount.Should().Be(1);
    }

    [Fact]
    public void Entry_IsInCooldown_TrueWhenRecentlyRateLimited()
    {
        var entry = ApiKeyPoolEntry.Create("key", "test", cooldownSeconds: 60);
        entry.MarkRateLimited();

        entry.IsInCooldown.Should().BeTrue();
    }

    [Fact]
    public void Entry_IsInCooldown_FalseWhenNeverRateLimited()
    {
        var entry = ApiKeyPoolEntry.Create("key", "test");
        entry.IsInCooldown.Should().BeFalse();
    }

    [Fact]
    public void Entry_InsufficientQuota_DisablesPermanently()
    {
        var entry = ApiKeyPoolEntry.Create("key", "test");
        entry.MarkRateLimited("insufficient_quota");

        entry.IsPermanentlyDisabled.Should().BeTrue();
        entry.LastErrorType.Should().Be("insufficient_quota");
        entry.IsEligible.Should().BeFalse();
    }

    [Fact]
    public void Entry_RateLimitExceeded_KeepsEligible()
    {
        var entry = ApiKeyPoolEntry.Create("key", "test");
        entry.MarkRateLimited("rate_limit_exceeded");

        entry.IsPermanentlyDisabled.Should().BeFalse();
        entry.IsEligible.Should().BeTrue();
        entry.IsInCooldown.Should().BeTrue();
    }

    [Fact]
    public void Entry_SupportsModel_NullAllowedModels_ReturnsTrue()
    {
        var entry = ApiKeyPoolEntry.Create("key", "test");
        entry.SupportsModel("gpt-5.4-mini").Should().BeTrue();
        entry.SupportsModel("any-model").Should().BeTrue();
    }

    [Fact]
    public void Entry_SupportsModel_WithFilter()
    {
        var entry = ApiKeyPoolEntry.Create("key", "test");
        entry.AllowedModels = ["gpt-5.4-mini", "gpt-4o"];

        entry.SupportsModel("gpt-5.4-mini").Should().BeTrue();
        entry.SupportsModel("gpt-4o").Should().BeTrue();
        entry.SupportsModel("claude-sonnet-4").Should().BeFalse();
    }

    [Fact]
    public void Entry_Success_ResetsErrorType()
    {
        var entry = ApiKeyPoolEntry.Create("key", "test");
        entry.MarkRateLimited("rate_limit_exceeded");
        entry.MarkSuccess();

        entry.LastErrorType.Should().BeNull();
        entry.ConsecutiveRateLimits.Should().Be(0);
    }

    [Fact]
    public void Pool_GetEligibleKeys_ExcludesDisabled()
    {
        var pool = ApiKeyPool.Create(ProviderId, TenantId);
        var good = ApiKeyPoolEntry.Create("key-0", "k0");
        var bad = ApiKeyPoolEntry.Create("key-1", "k1");
        bad.MarkRateLimited("insufficient_quota");
        pool.Entries.Add(good);
        pool.Entries.Add(bad);

        var eligible = pool.Entries.Where(e => e.IsEligible).ToList();
        eligible.Should().HaveCount(1);
        eligible[0].Label.Should().Be("k0");
    }
}
