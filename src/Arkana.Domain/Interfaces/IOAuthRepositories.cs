using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

/// <summary>Repository for <see cref="OAuthProviderConfig"/> OAuth client templates.</summary>
public interface IOAuthProviderConfigRepository
{
    Task<IReadOnlyList<OAuthProviderConfig>> GetAllAsync(CancellationToken ct = default);
    Task<OAuthProviderConfig?> GetByCodeAsync(string providerCode, CancellationToken ct = default);
    Task<OAuthProviderConfig?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(OAuthProviderConfig config, CancellationToken ct = default);
}

/// <summary>Repository for <see cref="OAuthPendingFlow"/> in-flight exchange state.</summary>
public interface IOAuthPendingFlowRepository
{
    Task<OAuthPendingFlow?> GetByStateAsync(string state, Guid? tenantId, CancellationToken ct = default);
    Task<OAuthPendingFlow?> GetActiveByProviderCodeAsync(string aiProviderCode, Guid tenantId, CancellationToken ct = default);
    Task DeletePendingForAccountAsync(string aiProviderCode, Guid tenantId, CancellationToken ct = default);
    Task AddAsync(OAuthPendingFlow flow, CancellationToken ct = default);
    Task UpdateAsync(OAuthPendingFlow flow, CancellationToken ct = default);
    Task<bool> PersistDeviceCredentialsAsync(OAuthPendingFlow flow, Guid tenantId, DateTimeOffset claimedAt, CancellationToken ct = default);
    Task<bool> PersistCompletionFailureAsync(OAuthPendingFlow flow, Guid tenantId, DateTimeOffset claimAt, bool deviceCompletionClaim, CancellationToken ct = default);
    Task<bool> TryClaimAsync(Guid id, Guid tenantId, DateTimeOffset claimedAt, CancellationToken ct = default);
    Task<bool> TryReleaseClaimAsync(Guid id, Guid tenantId, DateTimeOffset claimedAt, CancellationToken ct = default);
    Task<bool> TryClaimCompletionAsync(Guid id, Guid tenantId, DateTimeOffset claimedAt, CancellationToken ct = default);
    Task<bool> TryReleaseCompletionClaimAsync(Guid id, Guid tenantId, DateTimeOffset claimedAt, CancellationToken ct = default);
    Task DeleteAsync(Guid id, Guid tenantId, CancellationToken ct = default);
}

/// <summary>Repository for per-provider <see cref="ProviderOAuthToken"/> connection state.</summary>
public interface IProviderOAuthTokenRepository
{
    Task<ProviderOAuthToken?> GetByProviderAsync(Guid aiProviderId, Guid tenantId, CancellationToken ct = default);

    /// <summary>System scheduler query; each returned row carries its tenant for an isolated refresh.</summary>
    Task<IReadOnlyList<ProviderOAuthToken>> GetConnectedNeedingRefreshAsync(TimeSpan slack, CancellationToken ct = default);
    Task AddAsync(ProviderOAuthToken token, CancellationToken ct = default);
    Task UpdateAsync(ProviderOAuthToken token, CancellationToken ct = default);
}
