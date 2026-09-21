using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Repository for managing API key pools — multi-key rotation per provider.
/// </summary>
public interface IApiKeyPoolRepository
{
    Task<ApiKeyPool?> GetByProviderAsync(Guid aiProviderId, Guid tenantId, CancellationToken ct = default);
    Task<ApiKeyPool?> GetByIdAsync(Guid poolId, CancellationToken ct = default);
    Task<IReadOnlyList<ApiKeyPool>> GetAllAsync(Guid tenantId, CancellationToken ct = default);
    Task CreateAsync(ApiKeyPool pool, CancellationToken ct = default);
    Task UpdateAsync(ApiKeyPool pool, CancellationToken ct = default);
    Task DeleteAsync(Guid poolId, CancellationToken ct = default);
}
