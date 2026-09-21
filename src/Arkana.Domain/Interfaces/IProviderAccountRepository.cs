using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

public interface IProviderAccountRepository
{
    Task<ProviderAccount?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default);
    /// <summary>
    /// Tracker-independent read for version fencing. Returns the committed row
    /// without consulting or seeding the EF change tracker, so a shared or
    /// long-lived DbContext can never fence a reservation with a stale
    /// tracked snapshot.
    /// </summary>
    Task<ProviderAccount?> GetByIdNoTrackingAsync(Guid tenantId, Guid id, CancellationToken ct = default);
    Task<ProviderAccount?> GetByCodeAsync(Guid tenantId, string code, CancellationToken ct = default);
    Task<IReadOnlyList<ProviderAccount>> GetForProviderAsync(Guid tenantId, Guid providerId, bool includeDeleted = false, CancellationToken ct = default);
    Task<IReadOnlyList<ProviderAccount>> GetHealthyAsync(Guid tenantId, Guid providerId, DateTimeOffset? at = null, CancellationToken ct = default);
    Task AddAsync(ProviderAccount account, CancellationToken ct = default);
    Task UpdateAsync(ProviderAccount account, CancellationToken ct = default);
    Task<IAsyncDisposable> AcquireMutationLeaseAsync(Guid tenantId, Guid id, CancellationToken ct = default);
    Task<bool> TryReserveVersionAsync(Guid tenantId, Guid id, Guid expectedVersion, Guid reservationVersion, CancellationToken ct = default);
    Task<bool> TryUpdateDataPlaneHealthAsync(ProviderAccount account, Guid expectedVersion, CancellationToken ct = default);
}
