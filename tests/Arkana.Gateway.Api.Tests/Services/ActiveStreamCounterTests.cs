namespace Arkana.Gateway.Api.Tests.Services;

using Arkana.Gateway.Api.Services;

public sealed class ActiveStreamCounterTests
{
    [Fact]
    public void InitialCount_ShouldBeZero()
    {
        var counter = new ActiveStreamCounter();
        counter.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void Increment_ShouldIncreaseCountByOne()
    {
        var counter = new ActiveStreamCounter();
        counter.Increment();
        counter.ActiveCount.Should().Be(1);
    }

    [Fact]
    public void Decrement_ShouldDecreaseCountByOne()
    {
        var counter = new ActiveStreamCounter();
        counter.Increment();
        counter.Increment();
        counter.Decrement();
        counter.ActiveCount.Should().Be(1);
    }

    [Fact]
    public void DecrementBelowZero_ShouldAllowNegativeValues()
    {
        var counter = new ActiveStreamCounter();
        counter.Decrement();
        counter.ActiveCount.Should().Be(-1);
    }

    [Fact]
    public void MultipleIncrements_ShouldReflectTotal()
    {
        var counter = new ActiveStreamCounter();
        const int count = 100;
        for (var i = 0; i < count; i++)
            counter.Increment();
        counter.ActiveCount.Should().Be(count);
    }

    [Fact]
    public void MultipleDecrements_ShouldReflectTotal()
    {
        var counter = new ActiveStreamCounter();
        const int count = 100;
        for (var i = 0; i < count; i++)
            counter.Increment();
        for (var i = 0; i < count; i++)
            counter.Decrement();
        counter.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void OnCountChanged_ShouldFireOnIncrement()
    {
        var counter = new ActiveStreamCounter();
        var fired = false;
        counter.OnCountChanged += () => fired = true;
        counter.Increment();
        fired.Should().BeTrue();
    }

    [Fact]
    public void OnCountChanged_ShouldFireOnDecrement()
    {
        var counter = new ActiveStreamCounter();
        counter.Increment();
        var fired = false;
        counter.OnCountChanged += () => fired = true;
        counter.Decrement();
        fired.Should().BeTrue();
    }

    [Fact]
    public void OnCountChanged_ShouldNotFireWhenNoChange()
    {
        var counter = new ActiveStreamCounter();
        var fired = false;
        counter.OnCountChanged += () => fired = true;
        fired.Should().BeFalse();
    }

    [Fact]
    public void MultipleSubscribers_ShouldAllFire()
    {
        var counter = new ActiveStreamCounter();
        var fired1 = false;
        var fired2 = false;
        counter.OnCountChanged += () => fired1 = true;
        counter.OnCountChanged += () => fired2 = true;
        counter.Increment();
        fired1.Should().BeTrue();
        fired2.Should().BeTrue();
    }

    [Fact]
    public void ConcurrentIncrements_ShouldBeThreadSafe()
    {
        var counter = new ActiveStreamCounter();
        const int threadsCount = 8;
        const int iterationsPerThread = 1000;
        var threads = new Thread[threadsCount];

        for (var i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                for (var j = 0; j < iterationsPerThread; j++)
                    counter.Increment();
            });
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        counter.ActiveCount.Should().Be(threadsCount * iterationsPerThread);
    }

    [Fact]
    public void ConcurrentDecrements_ShouldBeThreadSafe()
    {
        var counter = new ActiveStreamCounter();
        const int threadsCount = 8;
        const int iterationsPerThread = 1000;
        var target = threadsCount * iterationsPerThread;

        // First increment to a high value
        for (var i = 0; i < target; i++)
            counter.Increment();

        var threads = new Thread[threadsCount];
        for (var i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                for (var j = 0; j < iterationsPerThread; j++)
                    counter.Decrement();
            });
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        counter.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void InterleavedIncrementDecrement_ShouldMaintainCorrectCount()
    {
        var counter = new ActiveStreamCounter();

        // Simulate realistic streaming pattern: start 10 streams, end 5, start 3 more, end 8
        for (var i = 0; i < 10; i++) counter.Increment();
        for (var i = 0; i < 5; i++) counter.Decrement();
        for (var i = 0; i < 3; i++) counter.Increment();
        for (var i = 0; i < 8; i++) counter.Decrement();

        counter.ActiveCount.Should().Be(0);
    }
}
