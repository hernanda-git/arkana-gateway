namespace Arkana.Domain.Entities;

/// <summary>
/// Global rate limit defaults (single row). Replaces the old
/// <c>appsettings.json → RateLimiter</c> config so defaults can
/// be changed at runtime from the Settings UI without a restart.
/// If no row exists, the <see cref="Default"/> values are used
/// (same as the old hardcoded defaults).
/// </summary>
public sealed class GlobalRateLimit
{
    public Guid Id { get; private set; }

    /// <summary>Master enable flag. When false, the limiter is a no-op.</summary>
    public bool Enabled { get; set; } = true;

    public int DefaultRequestsPerMinute { get; set; } = 60;
    public int DefaultTokensPerMinute { get; set; } = 1_000_000;
    public int DefaultMaxConcurrent { get; set; } = 5;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private GlobalRateLimit() { } // EF Core

    public static GlobalRateLimit Create() => new() { Id = Guid.NewGuid() };

    /// <summary>Factory defaults — used as fallback when no DB row exists.</summary>
    public static GlobalRateLimit Default => new()
    {
        Id = Guid.Empty,
        Enabled = true,
        DefaultRequestsPerMinute = 60,
        DefaultTokensPerMinute = 1_000_000,
        DefaultMaxConcurrent = 5,
    };
}
