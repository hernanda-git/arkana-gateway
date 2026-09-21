namespace Arkana.Application.Features.Chat;

/// <summary>
/// Configuration for the response cache (PERF-ARKANA-002).
///
/// Bound from configuration section <c>ResponseCache</c>:
/// <code>
/// "ResponseCache": {
///   "Enabled": true,
///   "DefaultTtlSeconds": 300
/// }
/// </code>
///
/// The default TTL of 5 minutes is intentionally short — the cache is
/// for collapsing rapid repetition (agent loops, CI re-runs, dashboard
/// pings), not for long-term storage. Longer values risk masking
/// provider/model updates behind stale responses.
/// </summary>
public sealed class ResponseCacheOptions
{
    /// <summary>
    /// Configuration section name for binding via
    /// <c>IConfiguration.GetSection(...)</c>.
    /// </summary>
    public const string Section = "ResponseCache";

    /// <summary>
    /// Disabled by default. The Phase 2 implementation is exact-match
    /// only and works well for repeated identical requests, but some
    /// callers explicitly want fresh responses on every call (e.g.,
    /// A/B testing, prompt iteration). Flip to <c>true</c> in
    /// appsettings.json to enable.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Default TTL in seconds. Zero or negative means "do not cache"
    /// (a request can still be served from a previously-cached entry
    /// within its own TTL, but no new entries are written).
    /// </summary>
    public int DefaultTtlSeconds { get; init; } = 300;

    public TimeSpan DefaultTtl => TimeSpan.FromSeconds(DefaultTtlSeconds);
}
