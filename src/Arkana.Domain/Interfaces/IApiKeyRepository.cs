namespace Arkana.Domain.Interfaces;

/// <summary>
/// Repository for managing API keys.
/// </summary>
public interface IApiKeyRepository
{
    Task<Entities.ApiKey?> GetByKeyHashAsync(string keyHash, CancellationToken ct = default);
    Task AddAsync(Entities.ApiKey apiKey, CancellationToken ct = default);
    Task<IReadOnlyList<Entities.ApiKey>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Entities.ApiKey>> GetAllAsync(Guid tenantId, CancellationToken ct = default);
    Task UpdateAsync(Entities.ApiKey apiKey, CancellationToken ct = default);
}
