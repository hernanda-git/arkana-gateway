namespace Arkana.Domain.Services;

/// <summary>
/// Per-API-key rate limiter (SEC-ARKANA-005). Two surfaces:
///
///   1. <see cref="TryAcquireSlotAsync"/> — a "pre-flight" check
///      for RPM and concurrency. Returns a <see cref="RateLimitLease"/>
///      that the caller <c>Dispose</c>s when the request finishes.
///      On dispose, the concurrency slot is released.
///
///   2. <see cref="RecordTokenUsageAsync"/> — a "post-flight"
///      charge against the TPM bucket. Called by the chat handler
///      after the upstream provider returns.
///
/// Why post-flight for TPM but pre-flight for the others? RPM and
/// concurrency are known up-front (the request is what it is);
/// TPM depends on the response size, which we don't know until
/// the upstream answers. Over-budget-by-one on TPM is fine; the
/// next request will catch the overage and reject.
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Acquire a request slot. Returns a lease on success (caller
    /// must <c>await using</c>); returns <c>null</c> on RPM or
    /// concurrency limit hit (caller should 429 with the
    /// <see cref="RateLimitDecision"/> reason).
    /// </summary>
    ValueTask<RateLimitLease?> TryAcquireSlotAsync(string apiKeyName, CancellationToken ct = default);

    /// <summary>
    /// Charge token usage against the per-key TPM bucket. Returns
    /// false if the charge would exceed the limit; the caller can
    /// decide whether to reject retroactively (we currently log
    /// and accept, since the response is already on its way back
    /// to the client).
    /// </summary>
    bool RecordTokenUsageAsync(string apiKeyName, int inputTokens, int outputTokens);
}

/// <summary>
/// What the limiter decided. Carries the rejection reason so
/// the HTTP layer can build a useful 429 body (e.g. include
/// <c>Retry-After</c> for the right dimension).
/// </summary>
public enum RateLimitReason
{
    None,
    RequestsPerMinute,
    TokensPerMinute,
    Concurrency,
}

/// <summary>Result of a pre-flight check (SEC-ARKANA-005).</summary>
public sealed record RateLimitDecision(RateLimitReason Reason, int? RetryAfterSeconds);

/// <summary>
/// Opaque handle returned by <see cref="IRateLimiter.TryAcquireSlotAsync"/>.
/// The lease is the bookkeeping for the concurrency slot; the
/// <c>DisposeAsync</c> releases it.
/// </summary>
public sealed class RateLimitLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _onDispose;
    private int _disposed;

    public RateLimitLease(Func<ValueTask> onDispose)
    {
        _onDispose = onDispose;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _onDispose();
    }
}
