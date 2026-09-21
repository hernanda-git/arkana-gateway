namespace Arkana.Domain.Services;

/// <summary>
/// Retry policy for transient failures. Implements exponential backoff
/// with full jitter to spread retry storms across a fleet of gateway
/// instances when an upstream provider has a brief outage.
///
/// Why full jitter (vs decorrelated jitter or equal jitter):
///   - The AWS Architecture Blog (Marc Brooker, 2015) showed full jitter
///     produces the best aggregate latency in retry-storm simulations
///     because it spreads each retry attempt across the full backoff
///     window, reducing synchronized thundering-herd effects.
///   - Simpler to reason about: sleep = Random(0, base * 2^attempt).
///   - Trivially bounded — never sleeps longer than the cap, regardless
///     of attempt count.
///
/// Configuration:
///   - MaxAttempts: total tries (1 = no retry, 2 = one retry, etc.)
///   - BaseDelay: starting delay before the first retry
///   - MaxDelay: cap on any single delay (e.g., 30s prevents pathological waits)
///   - JitterSeed: optional deterministic seed for reproducible tests
/// </summary>
public sealed class RetryPolicy
{
    public int MaxAttempts { get; }
    public TimeSpan BaseDelay { get; }
    public TimeSpan MaxDelay { get; }
    private readonly Random _random;

    /// <summary>
    /// Default policy: 3 attempts total, 500ms base, 30s cap, full jitter.
    /// Reasonable for upstream AI provider calls where most transient
    /// failures resolve in 1-2 seconds.
    /// </summary>
    public static RetryPolicy Default { get; } = new(
        maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(500),
        maxDelay: TimeSpan.FromSeconds(30));

    /// <summary>
    /// Test policy: 2 attempts total, 10ms base, 100ms cap — fast for unit tests.
    /// </summary>
    public static RetryPolicy Test { get; } = new(
        maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(10),
        maxDelay: TimeSpan.FromMilliseconds(100));

    public RetryPolicy(int maxAttempts, TimeSpan baseDelay, TimeSpan maxDelay, int? jitterSeed = null)
    {
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Must be at least 1.");
        if (baseDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseDelay), "Must be non-negative.");
        if (maxDelay < baseDelay)
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "Must be >= baseDelay.");

        MaxAttempts = maxAttempts;
        BaseDelay = baseDelay;
        MaxDelay = maxDelay;
        _random = jitterSeed.HasValue ? new Random(jitterSeed.Value) : new Random();
    }

    /// <summary>
    /// Calculate the delay before the next retry attempt using full jitter.
    /// </summary>
    /// <param name="attempt">
    /// Zero-based attempt index. 0 = the delay before retry #1 (i.e., the
    /// first retry after the initial attempt failed).
    /// </param>
    public TimeSpan GetDelay(int attempt)
    {
        if (attempt < 0)
            throw new ArgumentOutOfRangeException(nameof(attempt), "Must be non-negative.");

        // Full jitter: sleep = Random(0, min(cap, base * 2^attempt))
        var exp = Math.Min(MaxDelay.TotalMilliseconds,
            BaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
        var jitteredMs = _random.NextDouble() * exp;
        return TimeSpan.FromMilliseconds(jitteredMs);
    }

    /// <summary>
    /// Run an async operation with retry. The predicate decides whether a
    /// given result warrants a retry (true = retry, false = return as-is).
    /// Cancellation is honored — the loop checks the token between attempts.
    /// </summary>
    public async Task<RetryResult<T>> ExecuteAsync<T>(
        Func<int, CancellationToken, Task<T>> operation,
        Func<T, bool> shouldRetry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(shouldRetry);

        T? last = default;
        Exception? lastException = null;
        int attempt = 0;
        var producedResult = false;

        while (attempt < MaxAttempts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await operation(attempt, ct);
                last = result;
                producedResult = true;

                if (!shouldRetry(result))
                {
                    return new RetryResult<T>(result, attempt + 1, succeeded: true);
                }

                if (attempt + 1 >= MaxAttempts)
                {
                    // Out of attempts — return the last result without a retry
                    return new RetryResult<T>(result, attempt + 1, succeeded: false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt + 1 >= MaxAttempts) break;
            }

            // Backoff before next attempt
            var delay = GetDelay(attempt);
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            attempt++;
        }

        // If every attempt threw, there is no result to hand back. Rethrow the last
        // exception instead of fabricating a default Value: a default/null Value in a
        // RetryResult<T> previously escaped to callers and dereferenced into an
        // unhandled NullReferenceException (HTTP 500) — for example when a provider
        // connector threw on all attempts (DB outage, broker client fault, …).
        // A flag (not a null check) is required because T may be a value type whose
        // default is not null.
        if (!producedResult && lastException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(lastException).Throw();

        return new RetryResult<T>(last!, MaxAttempts, succeeded: false, lastException);
    }
}

/// <summary>
/// Result of a retry-wrapped operation. <see cref="Succeeded"/> is true if
/// the operation completed without needing a retry OR succeeded within the
/// retry budget. <see cref="Attempts"/> is the count of operations actually
/// run (1 = no retry needed).
/// </summary>
public sealed class RetryResult<T>
{
    public T Value { get; }
    public int Attempts { get; }
    public bool Succeeded { get; }
    public Exception? LastException { get; }

    public RetryResult(T value, int attempts, bool succeeded, Exception? lastException = null)
    {
        Value = value;
        Attempts = attempts;
        Succeeded = succeeded;
        LastException = lastException;
    }
}
