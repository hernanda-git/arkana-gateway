using Arkana.Domain.Services;
using FluentAssertions;

namespace Arkana.Domain.Tests.Services;

public sealed class RetryPolicyTests
{
    [Fact]
    public void Constructor_RejectsZeroMaxAttempts()
    {
        Action act = () => _ = new RetryPolicy(0, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_RejectsNegativeBaseDelay()
    {
        Action act = () => _ = new RetryPolicy(3, TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_RejectsMaxLessThanBase()
    {
        Action act = () => _ = new RetryPolicy(3, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0, 0, 1000)]      // 1st retry: up to 500ms
    [InlineData(1, 0, 2000)]      // 2nd retry: up to 1000ms
    [InlineData(2, 0, 4000)]      // 3rd retry: up to 2000ms
    [InlineData(10, 0, 5000)]     // capped at MaxDelay
    public void GetDelay_RespectsBaseCapAndExponential(int attempt, double minMs, double maxMs)
    {
        var policy = new RetryPolicy(
            maxAttempts: 5,
            baseDelay: TimeSpan.FromMilliseconds(500),
            maxDelay: TimeSpan.FromMilliseconds(5000),
            jitterSeed: 42);

        for (int i = 0; i < 50; i++)
        {
            var delay = policy.GetDelay(attempt).TotalMilliseconds;
            delay.Should().BeGreaterThanOrEqualTo(minMs);
            delay.Should().BeLessThanOrEqualTo(maxMs);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FirstAttemptSucceeds_NoRetry()
    {
        var policy = RetryPolicy.Test;
        var calls = 0;

        var result = await policy.ExecuteAsync<int>(
            (_, _) => { calls++; return Task.FromResult(42); },
            shouldRetry: _ => false, // predicate says: don't retry anything
            ct: default);

        result.Succeeded.Should().BeTrue();
        result.Value.Should().Be(42);
        result.Attempts.Should().Be(1);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRetryFalse_StopsImmediately()
    {
        var policy = RetryPolicy.Test;
        var calls = 0;

        var result = await policy.ExecuteAsync<int>(
            (_, _) => { calls++; return Task.FromResult(-1); },
            shouldRetry: _ => false,
            ct: default);

        result.Succeeded.Should().BeTrue(); // didn't need to retry
        result.Value.Should().Be(-1);
        result.Attempts.Should().Be(1);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_RetriesUntilSuccess()
    {
        var policy = new RetryPolicy(5, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(50), 42);
        var calls = 0;

        var result = await policy.ExecuteAsync<int>(
            (_, _) =>
            {
                calls++;
                return Task.FromResult(calls < 3 ? -1 : 42);
            },
            shouldRetry: v => v < 0,
            ct: default);

        result.Succeeded.Should().BeTrue();
        result.Value.Should().Be(42);
        result.Attempts.Should().Be(3);
        calls.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_ExhaustsBudget_ReportsFailure()
    {
        var policy = new RetryPolicy(3, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(20), 42);
        var calls = 0;

        var result = await policy.ExecuteAsync<int>(
            (_, _) => { calls++; return Task.FromResult(-1); },
            shouldRetry: _ => true,
            ct: default);

        result.Succeeded.Should().BeFalse();
        result.Value.Should().Be(-1);
        result.Attempts.Should().Be(3);
        calls.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionTreatedAsRetryable()
    {
        // Exceptions count as "needs retry"; the predicate is only consulted
        // for non-exception results. After 2 transient exceptions, the 3rd
        // call returns 42 — the predicate says "retry anything", so the
        // policy uses its last attempt and reports the budget exhausted.
        // The caller is expected to write a predicate that recognizes a
        // good result (e.g., "v < 0 => retry" not "_ => retry").
        var policy = new RetryPolicy(3, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(20), 42);
        var calls = 0;

        var result = await policy.ExecuteAsync<int>(
            (_, _) =>
            {
                calls++;
                if (calls < 3) throw new InvalidOperationException("transient");
                return Task.FromResult(42);
            },
            shouldRetry: _ => true,
            ct: default);

        result.Succeeded.Should().BeFalse(); // budget exhausted (predicate always retries)
        result.Value.Should().Be(42);
        result.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationPropagatesImmediately()
    {
        var policy = new RetryPolicy(5, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await policy.ExecuteAsync<int>(
            (_, _) => Task.FromResult(42),
            shouldRetry: _ => true,
            ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionsExhausted_RethrowsTheLastException()
    {
        // Contract: when every attempt throws, there is no result to return.
        // The policy rethrows the final exception instead of handing back a
        // default/null Value (which callers dereferenced into an unhandled
        // NullReferenceException -> HTTP 500).
        var policy = new RetryPolicy(2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5), 42);
        var calls = 0;

        Func<Task> act = async () => { await policy.ExecuteAsync<int>(
            (_, _) =>
            {
                calls++;
                throw new InvalidOperationException($"call {calls}");
            },
            shouldRetry: _ => true,
            ct: default); };

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Be("call 2");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_MixedThrowThenResult_ReturnsTheResult()
    {
        var policy = new RetryPolicy(3, TimeSpan.Zero, TimeSpan.Zero, 42);
        var calls = 0;

        var result = await policy.ExecuteAsync<string>(
            (_, _) =>
            {
                calls++;
                if (calls == 1)
                    throw new InvalidOperationException("transient");
                return Task.FromResult("ok");
            },
            shouldRetry: value => value != "ok",
            ct: default);

        result.Value.Should().Be("ok");
        result.Attempts.Should().Be(2);
        result.Succeeded.Should().BeTrue();
    }
}
