using Arkana.Domain.Services;
using FluentAssertions;

namespace Arkana.Domain.Tests.Services;

public sealed class CooldownTrackerTests
{
    [Fact]
    public void IsOnCooldown_UnknownKey_ReturnsNull()
    {
        var tracker = new CooldownTracker();

        tracker.IsOnCooldown("nonexistent").Should().BeNull();
    }

    [Fact]
    public void RecordFailure_BelowThreshold_DoesNotOpenCooldown()
    {
        var tracker = new CooldownTracker(CooldownOptions.Test);

        // Test options: 2 failures within 5s opens a 1s cooldown
        tracker.RecordFailure("acct-1");
        tracker.IsOnCooldown("acct-1").Should().BeNull();

        // Still below threshold after one failure
        tracker.RecordFailure("acct-1");
        // The second failure opens the cooldown
        tracker.IsOnCooldown("acct-1").Should().NotBeNull();
    }

    [Fact]
    public void RecordFailure_AtThreshold_OpensCooldown()
    {
        var options = new CooldownOptions
        {
            Threshold = 2,
            Window = TimeSpan.FromSeconds(5),
            Duration = TimeSpan.FromSeconds(1)
        };
        var tracker = new CooldownTracker(options);

        tracker.RecordFailure("acct-1");
        tracker.RecordFailure("acct-1");

        var remaining = tracker.IsOnCooldown("acct-1");
        remaining.Should().NotBeNull();
        remaining!.Value.Should().BeGreaterThan(TimeSpan.Zero);
        remaining.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void RecordSuccess_ClearsCooldown()
    {
        var tracker = new CooldownTracker(CooldownOptions.Test);
        tracker.RecordFailure("acct-1");
        tracker.RecordFailure("acct-1");
        tracker.IsOnCooldown("acct-1").Should().NotBeNull();

        tracker.RecordSuccess("acct-1");

        tracker.IsOnCooldown("acct-1").Should().BeNull();
    }

    [Fact]
    public void IsOnCooldown_ExpiredCooldown_ReturnsNullAndClears()
    {
        var options = new CooldownOptions
        {
            Threshold = 1, // open on first failure
            Window = TimeSpan.FromSeconds(5),
            Duration = TimeSpan.FromMilliseconds(50)
        };
        var tracker = new CooldownTracker(options);

        tracker.RecordFailure("acct-1");
        tracker.IsOnCooldown("acct-1").Should().NotBeNull();

        // Wait past the cooldown
        Thread.Sleep(100);

        tracker.IsOnCooldown("acct-1").Should().BeNull();
    }

    [Fact]
    public void RecordFailure_OutsideWindow_ResetsCount()
    {
        var options = new CooldownOptions
        {
            Threshold = 3,
            Window = TimeSpan.FromMilliseconds(100),
            Duration = TimeSpan.FromSeconds(1)
        };
        var tracker = new CooldownTracker(options);

        tracker.RecordFailure("acct-1");
        tracker.RecordFailure("acct-1");
        // Window expires
        Thread.Sleep(150);

        // After the window, the count should reset
        tracker.RecordFailure("acct-1");
        tracker.IsOnCooldown("acct-1").Should().BeNull(); // only 1 in current window
    }

    [Fact]
    public void DifferentAccounts_TrackedIndependently()
    {
        var tracker = new CooldownTracker(CooldownOptions.Test);
        // acct-1 hits threshold
        tracker.RecordFailure("acct-1");
        tracker.RecordFailure("acct-1");
        // acct-2 only has one failure
        tracker.RecordFailure("acct-2");

        tracker.IsOnCooldown("acct-1").Should().NotBeNull();
        tracker.IsOnCooldown("acct-2").Should().BeNull();
    }

    [Fact]
    public void EmptyKey_IsIgnored()
    {
        var tracker = new CooldownTracker(CooldownOptions.Test);

        tracker.RecordFailure("");
        tracker.RecordFailure(null!);
        tracker.RecordSuccess("");

        tracker.IsOnCooldown("").Should().BeNull();
    }

    [Fact]
    public void Reset_ClearsAllTracking()
    {
        var tracker = new CooldownTracker(CooldownOptions.Test);
        tracker.RecordFailure("a");
        tracker.RecordFailure("a");
        tracker.IsOnCooldown("a").Should().NotBeNull();

        tracker.Reset();

        tracker.IsOnCooldown("a").Should().BeNull();
        tracker.TrackedAccountCount.Should().Be(0);
    }

    [Fact]
    public void TrackedAccountCount_ReflectsActiveKeys()
    {
        var tracker = new CooldownTracker(CooldownOptions.Test);

        tracker.RecordFailure("a");
        tracker.RecordFailure("b");
        tracker.RecordFailure("c");

        tracker.TrackedAccountCount.Should().Be(3);

        tracker.RecordSuccess("b");

        tracker.TrackedAccountCount.Should().Be(2);
    }

    [Fact]
    public void DefaultOptions_HasReasonableDefaults()
    {
        var opts = CooldownOptions.Default;
        opts.Threshold.Should().BeGreaterThan(0);
        opts.Window.Should().BeGreaterThan(TimeSpan.Zero);
        opts.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }
}
