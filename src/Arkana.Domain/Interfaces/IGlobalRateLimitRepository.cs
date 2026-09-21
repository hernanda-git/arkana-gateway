using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Repository for <see cref="GlobalRateLimit"/> — the single-row
/// table that stores runtime rate-limit defaults (no restart needed).
/// </summary>
public interface IGlobalRateLimitRepository
{
    /// <summary>Get the current config row, or null if none exists.</summary>
    Task<GlobalRateLimit?> GetAsync();

    /// <summary>Upsert (insert if missing, update if exists).</summary>
    Task UpsertAsync(GlobalRateLimit config);
}
