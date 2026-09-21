namespace Arkana.Domain.Interfaces;

/// <summary>
/// Provides rate-limit configuration (global defaults + per-key overrides).
/// Implementations may cache aggressively (30s TTL is fine) since this
/// is called on every request's hot path.
/// </summary>
public interface IRateLimitConfigProvider
{
    /// <summary>Master enable flag.</summary>
    Task<bool> IsEnabledAsync();

    /// <summary>Global defaults (used when a key has no override).</summary>
    Task<(int Rpm, int Tpm, int MaxConcurrent)> GetDefaultsAsync();

    /// <summary>Per-key overrides, or nulls if no override set.</summary>
    Task<(int? Rpm, int? Tpm, int? MaxConcurrent)> GetKeyOverridesAsync(string apiKeyName);
}
