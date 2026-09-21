namespace Arkana.Domain.Services;

/// <summary>
/// Configuration for per-API-key rate limiting (SEC-ARKANA-005).
/// All three limits (RPM, TPM, concurrency) are checked
/// independently. A request that fails ANY check is rejected with
/// HTTP 429.
///
/// Why three dimensions, not one?
///   - **Concurrency** is about not blowing the upstream quota —
///     even a slow-but-cheap request can saturate OpenCode/DeepSeek
///     if 200 keys each fire 5 requests simultaneously.
///   - **RPM** is about request volume — the unit of work the
///     upstream has to authenticate and route.
///   - **TPM** is about cost — a single 100k-context request
///     dwarfs 1000 short ones, and TPM is the only way to capture
///     that. Charging it post-completion (not pre-flight) is fine
///     because over-budget-by-one is a soft failure we can absorb.
/// </summary>
public sealed class RateLimiterOptions
{
    /// <summary>appsettings section name. Always use the constant.</summary>
    public const string Section = "RateLimiter";

    /// <summary>
    /// Master enable flag. When false, the limiter is a no-op
    /// (matches the request straight through). Useful for tests
    /// and emergency disable.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default RPM for keys that don't have an explicit override.
    /// 60 RPM = 1 RPS per key, which is a sane starting point for
    /// a multi-tenant gateway.
    /// </summary>
    public int DefaultRequestsPerMinute { get; set; } = 60;

    /// <summary>
    /// Default TPM. ~1M tokens/min covers "moderate use" for any
    /// single key. Heavy users get explicit higher limits via
    /// <see cref="Overrides"/>.
    /// </summary>
    public int DefaultTokensPerMinute { get; set; } = 1_000_000;

    /// <summary>
    /// Default in-flight concurrency. 5 in flight is the sweet spot
    /// for most upstream APIs (OpenAI, Anthropic, DeepSeek all
    /// allow more, but a single key saturating their pool is rude).
    /// </summary>
    public int DefaultMaxConcurrent { get; set; } = 5;

    /// <summary>
    /// Per-key overrides. Matched by API key name (case-insensitive).
    /// Keys not present here fall back to the Default* values.
    /// </summary>
    public Dictionary<string, RateLimitOverride> Overrides { get; set; } = new();

    /// <summary>
    /// Background cleanup cadence. Stale per-key buckets are
    /// pruned every <see cref="CleanupInterval"/> to bound memory
    /// growth in long-running deployments with churning keys.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A bucket that hasn't been touched in this long is eligible
    /// for cleanup. Longer than any reasonable think-time, shorter
    /// than a daily report's patience.
    /// </summary>
    public TimeSpan BucketIdleTtl { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>Per-key rate-limit override (SEC-ARKANA-005).</summary>
public sealed class RateLimitOverride
{
    public int? RequestsPerMinute { get; set; }
    public int? TokensPerMinute { get; set; }
    public int? MaxConcurrent { get; set; }
}
