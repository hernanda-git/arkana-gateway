using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Persistence for the latest ChatGPT Codex usage snapshot per
/// (account provider × window). One row per pair — newest observation wins.
/// </summary>
public interface IAccountUsageSnapshotRepository
{
    /// <summary>Latest snapshots for one authenticated tenant, optionally filtered to account codes.</summary>
    Task<List<AccountUsageSnapshot>> GetByAccountCodesAsync(
        Guid tenantId, IEnumerable<string>? accountCodes = null, CancellationToken ct = default);

    /// <summary>
    /// Upserts one observation (insert or update the existing row for the pair).
    /// Returns true when a row was written.
    /// </summary>
    Task<bool> UpsertAsync(AccountUsageSnapshot snapshot, CancellationToken ct = default);
}
