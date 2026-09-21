using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Arkana.Application.Features.Chat;

/// <summary>
/// Executes a chat completion request against a primary provider, and on
/// failure walks a fallback chain of lower-priority providers until one
/// succeeds or all are exhausted.
///
/// Each provider in the chain is given a per-provider retry budget
/// (controlled by the injected <see cref="RetryPolicy"/>) so transient
/// blips on a single provider don't immediately fall through to the
/// next. This gives two layers of resilience:
///   - Within a provider: transient errors (5xx, 429, network) get
///     retried with exponential backoff + jitter.
///   - Across providers: sustained failures fall through to the next
///     lower-priority provider in the chain.
///
/// Cooldown (REL-ARKANA-003) adds a third layer:
///   - Accounts that fail repeatedly (5+ times within 60s by default)
///     are marked "on cooldown" for 30s. Calls to a cooling-down
///     account short-circuit immediately with a cooldown error,
///     skipping retries AND the actual HTTP request. This prevents
///     a failing provider from amplifying the outage via the gateway.
///
/// Chain semantics:
///   - Providers are sorted by Priority ASC (lower number = higher priority).
///   - The first provider is the primary. If it's on cooldown, the
///     executor skips it and tries the next provider.
///   - Within a non-cooldown provider, up to <see cref="RetryPolicy.MaxAttempts"/>
///     attempts are made before the chain advances.
///   - "Retryable" errors are determined by <see cref="IsRetryable"/>:
///     network failures, 5xx, 429 rate limits. 4xx (except 429) are NOT
///     retried — they indicate bad request, auth, or content policy issues
///     that won't be fixed by waiting.
///   - On success, the result of the first successful provider is returned.
///   - On total failure, the LAST error is returned so the caller sees the
///     most recent state.
///   - Fallback attempts are logged at Information; the original error and
///     each transition is captured for observability.
///
/// Why a separate executor and not in the handler:
///   - The SendChatHandler is already large (auth, pricing, logging). The
///     fallback logic is orthogonal and needs to be testable in isolation
///     without spinning up a MediatR pipeline.
/// </summary>
public sealed class FallbackChainExecutor
{
    private readonly ILogger<FallbackChainExecutor> _logger;
    private readonly RetryPolicy _retryPolicy;
    private readonly CooldownTracker _cooldown;

    /// <summary>
    /// Default constructor — uses <see cref="RetryPolicy.Default"/> and
    /// <see cref="CooldownOptions.Default"/>.
    /// </summary>
    public FallbackChainExecutor(ILogger<FallbackChainExecutor> logger)
        : this(logger, RetryPolicy.Default, new CooldownTracker())
    {
    }

    /// <summary>
    /// Constructor with explicit retry policy (used by production DI).
    /// </summary>
    public FallbackChainExecutor(ILogger<FallbackChainExecutor> logger, RetryPolicy retryPolicy)
        : this(logger, retryPolicy, new CooldownTracker())
    {
    }

    /// <summary>
    /// Full constructor — all dependencies injectable. Used by tests
    /// to control retry timing and cooldown state deterministically.
    /// </summary>
    public FallbackChainExecutor(
        ILogger<FallbackChainExecutor> logger,
        RetryPolicy retryPolicy,
        CooldownTracker cooldown)
    {
        _logger = logger;
        _retryPolicy = retryPolicy;
        _cooldown = cooldown;
    }

    /// <summary>
    /// Execute the request through the fallback chain. The first provider
    /// in <paramref name="providers"/> is the primary.
    /// </summary>
    /// <returns>
    /// A <see cref="FallbackExecution"/> containing the result and the
    /// provider that actually served the request. On total failure,
    /// <c>ServedBy</c> is the last provider tried.
    /// </returns>
    public async Task<FallbackExecution> ExecuteAsync(
        IReadOnlyList<IChatCompletionService> providers,
        ChatRequest request,
        CancellationToken ct = default)
    {
        if (providers is null || providers.Count == 0)
            throw new ArgumentException("At least one provider is required.", nameof(providers));

        var primary = providers[0];
        ChatResult? lastResult = null;
        IChatCompletionService? servedBy = null;
        // totalAttempts tracks the cumulative attempt count across all
        // providers in the chain — what gets reported in FallbackExecution
        // and used for observability.
        int totalAttempts = 0;

        for (int i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];
            var isPrimary = i == 0;
            var label = isPrimary ? "primary" : $"fallback #{i}";
            var accountKey = provider.ProviderName; // cooldown key = provider code

            _logger.LogDebug(
                "Executing chat via {Label} provider {Provider}",
                label, provider.ProviderName);

            // ── Cooldown check (REL-ARKANA-003) ───────────────────────
            // Skip the provider entirely if it's on cooldown. The failure
            // is recorded as a no-op, the request moves to the next link
            // in the chain. This avoids amplifying the outage by hammering
            // an already-failing upstream.
            if (_cooldown.IsOnCooldown(accountKey) is { } remaining)
            {
                _logger.LogInformation(
                    "Provider {Provider} is on cooldown for {Remaining}s — skipping",
                    provider.ProviderName, (int)remaining.TotalSeconds);

                lastResult = new ChatResult
                {
                    ErrorMessage = $"Provider '{provider.ProviderName}' is on cooldown for {(int)remaining.TotalSeconds}s",
                    Model = request.Model
                };
                servedBy = provider;
                continue; // try the next provider
            }

            // Wrap this provider in the retry policy. Each attempt within
            // the provider is one of MaxAttempts; only advance to the next
            // provider after the retry budget is exhausted.
            //
            // shouldRetry = "the result was a failure AND the error is
            // retryable". On non-retryable errors (e.g. 401), the retry
            // loop returns immediately with the failure result so the
            // chain can short-circuit.
            ChatResult result;
            int attempts;
            try
            {
                var retryResult = await _retryPolicy.ExecuteAsync(
                    operation: async (_, cancellation) =>
                        await provider.CompleteAsync(request, cancellation),
                    shouldRetry: r => !r.IsSuccess && IsRetryable(r),
                    ct: ct);
                result = retryResult.Value;
                attempts = retryResult.Attempts;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A provider that throws on every attempt (DB outage, broker client
                // fault, …) must surface as a sanitized failure result: an unhandled
                // exception here aborts the whole request as HTTP 500, and the raw
                // exception text must not reach the client.
                _logger.LogError(
                    "Provider {Provider} failed unexpectedly ({ExceptionType}).",
                    provider.ProviderName, ex.GetType().Name);
                result = new ChatResult
                {
                    ErrorMessage = $"Provider '{provider.ProviderName}' failed unexpectedly.",
                    Model = request.Model,
                    UpstreamStatus = 502,
                };
                attempts = _retryPolicy.MaxAttempts;
            }

            lastResult = result;
            servedBy = provider;
            totalAttempts += attempts;

            // ── Cooldown bookkeeping ─────────────────────────────
            // On success, clear any prior failure record for the account
            // so future calls see a clean slate. On failure, record it
            // so repeated failures can open a cooldown.
            if (result.IsSuccess)
            {
                _cooldown.RecordSuccess(accountKey);
            }
            else if (IsRetryable(result))
            {
                // Only count retryable failures toward cooldown — a
                // 401 from one caller shouldn't trigger cooldown for
                // everyone else using the same provider.
                _cooldown.RecordFailure(accountKey);
            }

            if (result.IsSuccess)
            {
                if (!isPrimary || attempts > 1)
                {
                    _logger.LogInformation(
                        "Chat succeeded via {Label} provider {Provider} after {Attempts} total attempt(s)",
                        label, provider.ProviderName, totalAttempts);
                }
                return new FallbackExecution(result, provider, totalAttempts);
            }

            // Failure — decide whether to try the next provider.
            if (i == providers.Count - 1)
            {
                _logger.LogError(
                    "All {Count} providers in the fallback chain failed after {TotalAttempts} total attempts.",
                    providers.Count, totalAttempts);
                return new FallbackExecution(result, provider, totalAttempts);
            }

            if (!IsRetryable(result))
            {
                _logger.LogWarning(
                    "Provider {Provider} returned non-retryable error. Stopping chain.",
                    provider.ProviderName);
                return new FallbackExecution(result, provider, totalAttempts);
            }

            _logger.LogWarning(
                "Provider {Provider} failed after {Attempts} attempt(s). Trying next in chain.",
                provider.ProviderName, attempts);
        }

        // Unreachable — the loop returns on the last iteration.
        return new FallbackExecution(
            lastResult ?? new ChatResult
            {
                ErrorMessage = "Fallback chain produced no result",
                Model = request.Model
            },
            servedBy ?? primary,
            totalAttempts);
    }

    /// <summary>
    /// Classify an error as retryable (worth trying the next provider) or
    /// terminal (don't waste time on a different provider — the request is
    /// fundamentally broken).
    /// </summary>
    /// <remarks>
    /// Heuristic: scan the error message for known terminal patterns.
    /// The patterns are matched case-insensitively against substrings
    /// of the error message, which is the most portable way to classify
    /// errors from many different HTTP clients and SDKs.
    /// </remarks>
    public static bool IsRetryable(ChatResult result)
    {
        if (result.IsSuccess) return false;
        if (result.StreamingCommitted) return false;
        var msg = result.ErrorMessage ?? string.Empty;

        // Request cancellation and missing/account-invalid credentials are
        // scoped to this request/account. Retrying another provider would
        // either resurrect a cancelled request or cross an ownership boundary.
        if (ContainsAny(msg, "cancelled", "canceled", "client disconnected",
            "credential is not configured", "reconnect is required",
            "routing is unavailable", "ownership is invalid"))
        {
            return false;
        }

        // ── Rate limiting / quota exhaustion is ALWAYS retryable ──────
        // This check runs FIRST and short-circuits, because the terminal
        // list below contains substrings ("quota", "403", "forbidden")
        // that also appear in genuine upstream-exhaustion messages.
        //
        // Why it matters (regression found 2026-08-06): OpenCode returns
        // 429 "GoUsageLimitError: Weekly usage limit reached" when the
        // workspace quota is spent. The word "quota" hit the terminal
        // list, so IsRetryable returned FALSE, the executor logged
        // "non-retryable error. Stopping chain." and NEVER advanced to
        // the fallback provider. A quota-exhausted primary is the single
        // most important case for failing over — it must not be terminal.
        //
        // Likewise a 403 RegionError (model unavailable in this region)
        // is terminal for THAT provider but a different provider may well
        // serve the request, so the chain should continue.
        if (ContainsAny(msg,
            "429", "rate limit", "rate_limit", "too many requests",
            "usage limit", "usagelimit", "quota exceeded", "over quota",
            "regionerror", "region error"))
        {
            return true;
        }

        // Terminal — bad request from the client side
        if (ContainsAny(msg, "invalid_api_key", "incorrect api key", "401",
            "unauthorized", "authentication", "forbidden", "403",
            "invalid_request", "context_length_exceeded", "context length",
            "tokens exceed", "max_tokens", "model_not_found", "model not found",
            "content_policy", "content policy", "content_filter",
            "validation", "schema", "400 bad request",
            "billing", "insufficient_quota", "quota", "payment required"))
        {
            return false;
        }

        // Retryable — provider/network issue
        // (network, timeout, 5xx, 429, "internal error", "service unavailable", etc.)
        return true;
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Build a fallback chain ordered by provider priority. The first
    /// provider is the primary; subsequent ones are tried in priority order.
    /// </summary>
    /// <param name="allProviders">All enabled providers in the system.</param>
    /// <param name="primary">The preferred/primary provider (or null for default).</param>
    /// <param name="allServices">
    /// All available <see cref="IChatCompletionService"/> implementations.
    /// </param>
    /// <param name="logger">
    /// Optional logger used to warn when an enabled provider has no matching
    /// registered service and is therefore dropped from the chain. Optional so
    /// existing callers and tests keep compiling unchanged.
    /// </param>
    public static IReadOnlyList<IChatCompletionService> BuildChain(
        IReadOnlyList<AiProvider> allProviders,
        AiProvider? primary,
        IReadOnlyList<IChatCompletionService> allServices,
        ILogger? logger = null,
        bool allowProviderFallback = true)
    {
        if (allProviders is null || allProviders.Count == 0) return [];
        if (allServices is null || allServices.Count == 0) return [];
        if (!allowProviderFallback && primary is null) return [];

        // Filter to enabled providers, sort by priority
        var enabled = allProviders
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.Priority)
            .ToList();

        if (enabled.Count == 0) return [];

        if (primary is not null && !allowProviderFallback)
            enabled = enabled.Where(p => p.Id == primary.Id).ToList();

        // Reorder so the primary provider is first (if specified and enabled)
        if (primary is not null)
        {
            var primaryEntry = enabled.FirstOrDefault(p =>
                p.Id == primary.Id ||
                p.Code.Equals(primary.Code, StringComparison.OrdinalIgnoreCase));
            if (primaryEntry is not null && !ReferenceEquals(primaryEntry, enabled[0]))
            {
                enabled.Remove(primaryEntry);
                enabled.Insert(0, primaryEntry);
            }
        }

        // Map providers to their services by name
        var result = new List<IChatCompletionService>(enabled.Count);
        var unmapped = new List<string>();
        foreach (var p in enabled)
        {
            var svc = allServices.FirstOrDefault(s =>
                s.ProviderName.Equals(p.Code, StringComparison.OrdinalIgnoreCase) ||
                s.ProviderName.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (svc is null && p.Code.StartsWith("chatgpt", StringComparison.OrdinalIgnoreCase))
            {
                // ChatGPT uses one connector for the whole chatgpt-accN account
                // family; account selection happens inside ChatGptAccountPool.
                svc = allServices.FirstOrDefault(s =>
                    s.ProviderName.Equals("ChatGptCodex", StringComparison.OrdinalIgnoreCase));
            }
            if (svc is null && (p.Code.StartsWith("gemini-acc", StringComparison.OrdinalIgnoreCase)
                || p.Code.Equals("gemini-subscription", StringComparison.OrdinalIgnoreCase)))
            {
                // Broker-managed Gemini identities share the subscription broker;
                // never fall through to the direct HTTP/CLIProxy connector.
                svc = allServices.FirstOrDefault(s =>
                    s.ProviderName.Equals("gemini-subscription", StringComparison.OrdinalIgnoreCase));
            }
            if (svc is not null)
            {
                // Account-family rows can intentionally resolve to one shared
                // connector (ChatGPT/Gemini). Do not execute the same connector
                // twice merely because the catalog contains multiple accounts.
                if (!result.Contains(svc))
                    result.Add(svc);
            }
            else
            {
                // No IChatCompletionService is registered whose ProviderName
                // matches this provider's Code or Name. Continuing with a
                // shorter chain is still the right call — throwing here would
                // take down every request over one misconfigured row — but
                // this MUST NOT be silent.
                //
                // A DB row enabled with no matching service is the single most
                // likely cause of "the fallback chain is 1 deep and the gateway
                // died when its only upstream went down": the operator believes
                // N providers are configured, while N-1 were dropped here
                // without a word. Logging the names makes the misconfiguration
                // diagnosable from the logs instead of requiring a debugger.
                unmapped.Add($"{p.Name} (code '{p.Code}')");
            }
        }

        if (unmapped.Count > 0)
        {
            logger?.LogWarning(
                "Fallback chain: {Dropped} of {Total} enabled provider(s) were dropped because no " +
                "IChatCompletionService is registered for them: {Unmapped}. The effective chain depth " +
                "is {Depth} — if that is 1, a single upstream outage will fail every request.",
                unmapped.Count, enabled.Count, string.Join(", ", unmapped), result.Count);
        }

        return result;
    }
}

/// <summary>
/// Result of a fallback-chain execution. Carries the <see cref="ChatResult"/>
/// from whichever provider served the request (or the last error), along
/// with the provider that produced it and the total number of attempts.
/// </summary>
/// <param name="Result">The chat result (success or last error).</param>
/// <param name="ServedBy">The provider that produced the result.</param>
/// <param name="Attempts">Number of providers tried (1 = primary succeeded).</param>
public sealed record FallbackExecution(
    ChatResult Result,
    IChatCompletionService ServedBy,
    int Attempts);

