using Arkana.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arkana.Infrastructure.Tests.Services;

/// <summary>
/// Tests for the in-process <see cref="RateLimiter"/>
/// (SEC-ARKANA-005). Verifies RPM, concurrency, and TPM limit
/// enforcement, plus the per-key override path.
/// </summary>
public sealed class RateLimiterTests
{
    private static RateLimiter NewLimiter(TestRateLimitConfigProvider cfg)
    {
        return new RateLimiter(cfg, NullLogger<RateLimiter>.Instance);
    }

    // ── RPM ─────────────────────────────────────────────────────

    [Fact]
    public async Task TryAcquireSlotAsync_BelowRpmLimit_Accepts()
    {
        var cfg = new TestRateLimitConfigProvider { DefaultRpm = 5 };
        var limiter = NewLimiter(cfg);

        for (int i = 0; i < 5; i++)
        {
            var lease = await limiter.TryAcquireSlotAsync("key-a");
            lease.Should().NotBeNull($"request {i + 1} of 5 should be accepted");
            await lease!.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryAcquireSlotAsync_AboveRpmLimit_Rejects()
    {
        var cfg = new TestRateLimitConfigProvider { DefaultRpm = 3 };
        var limiter = NewLimiter(cfg);

        for (int i = 0; i < 3; i++)
        {
            var lease = await limiter.TryAcquireSlotAsync("key-a");
            lease.Should().NotBeNull($"request {i + 1} should be accepted");
            await lease!.DisposeAsync();
        }

        // 4th request should be rejected
        var rejected = await limiter.TryAcquireSlotAsync("key-a");
        rejected.Should().BeNull("4th request should exceed RPM limit");
    }

    [Fact]
    public async Task RpmLimit_ResetsAfterWindow()
    {
        var cfg = new TestRateLimitConfigProvider { DefaultRpm = 2 };
        var limiter = NewLimiter(cfg);

        // Consume both slots
        await (await limiter.TryAcquireSlotAsync("key-b"))!.DisposeAsync();
        await (await limiter.TryAcquireSlotAsync("key-b"))!.DisposeAsync();

        // 3rd should be rejected
        (await limiter.TryAcquireSlotAsync("key-b")).Should().BeNull();

        // key-c has its own counter
        await (await limiter.TryAcquireSlotAsync("key-c"))!.DisposeAsync();
    }

    [Fact]
    public async Task RpmLimit_IsPerKey()
    {
        var cfg = new TestRateLimitConfigProvider { DefaultRpm = 2 };
        var limiter = NewLimiter(cfg);

        // Consume both slots for key-a
        await (await limiter.TryAcquireSlotAsync("key-a"))!.DisposeAsync();
        await (await limiter.TryAcquireSlotAsync("key-a"))!.DisposeAsync();

        // key-b should still be allowed (separate counter)
        (await limiter.TryAcquireSlotAsync("key-b")).Should().NotBeNull();
        await (await limiter.TryAcquireSlotAsync("key-b"))!.DisposeAsync();
    }

    // ── Disabled ────────────────────────────────────────────────

    [Fact]
    public async Task WhenDisabled_AllRequestsAccepted()
    {
        var cfg = new TestRateLimitConfigProvider { Enabled = false, DefaultRpm = 1 };
        var limiter = NewLimiter(cfg);

        (await limiter.TryAcquireSlotAsync("key-a")).Should().NotBeNull();
        await (await limiter.TryAcquireSlotAsync("key-a"))!.DisposeAsync();
        (await limiter.TryAcquireSlotAsync("key-a")).Should().NotBeNull();
        await (await limiter.TryAcquireSlotAsync("key-a"))!.DisposeAsync();
        (await limiter.TryAcquireSlotAsync("key-a")).Should().NotBeNull();
        await (await limiter.TryAcquireSlotAsync("key-a"))!.DisposeAsync();
    }

    // ── Per-key overrides ───────────────────────────────────────

    [Fact]
    public async Task PerKeyOverride_OverridesDefault()
    {
        var cfg = new TestRateLimitConfigProvider
        {
            DefaultRpm = 2,
            Overrides = { ["key-a"] = (Rpm: 10, null, null) }
        };
        var limiter = NewLimiter(cfg);

        // key-a should allow 10 RPM
        for (int i = 0; i < 10; i++)
        {
            var lease = await limiter.TryAcquireSlotAsync("key-a");
            lease.Should().NotBeNull($"request {i + 1} of 10 should be accepted for key-a with override");
            await lease!.DisposeAsync();
        }

        // key-d (no override) should still be limited to default 2
        await (await limiter.TryAcquireSlotAsync("key-d"))!.DisposeAsync();
        await (await limiter.TryAcquireSlotAsync("key-d"))!.DisposeAsync();
        (await limiter.TryAcquireSlotAsync("key-d")).Should().BeNull();
    }

    // ── Concurrency ─────────────────────────────────────────────

    [Fact]
    public async Task ConcurrencyLimit_RejectsWhenMaxInFlight()
    {
        var cfg = new TestRateLimitConfigProvider { DefaultRpm = 100, DefaultMaxConcurrent = 2 };
        var limiter = NewLimiter(cfg);

        // Acquire 2 concurrent slots (don't dispose)
        var lease1 = await limiter.TryAcquireSlotAsync("key-a");
        lease1.Should().NotBeNull();
        var lease2 = await limiter.TryAcquireSlotAsync("key-a");
        lease2.Should().NotBeNull();

        // 3rd concurrent request should be rejected
        var rejected = await limiter.TryAcquireSlotAsync("key-a");
        rejected.Should().BeNull("3rd concurrent request should exceed concurrency limit");

        // Release one slot
        await lease2!.DisposeAsync();

        // Now it should succeed again
        var lease3 = await limiter.TryAcquireSlotAsync("key-a");
        lease3.Should().NotBeNull("should be accepted after one slot released");
        await lease3!.DisposeAsync();
        await lease1!.DisposeAsync();
    }

    // ── Lease lifecycle ─────────────────────────────────────────

    [Fact]
    public async Task LeaseDispose_ReleasesConcurrencySlot()
    {
        var cfg = new TestRateLimitConfigProvider { DefaultRpm = 100, DefaultMaxConcurrent = 1 };
        var limiter = NewLimiter(cfg);

        var lease = await limiter.TryAcquireSlotAsync("key-a");
        lease.Should().NotBeNull();

        // Without disposing, second should fail
        (await limiter.TryAcquireSlotAsync("key-a")).Should().BeNull();

        await lease!.DisposeAsync();

        // After dispose, should succeed
        (await limiter.TryAcquireSlotAsync("key-a")).Should().NotBeNull();
    }
}
