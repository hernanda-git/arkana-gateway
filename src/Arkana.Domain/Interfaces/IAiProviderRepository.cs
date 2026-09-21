using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Repository for managing AI provider configurations.
/// </summary>
public interface IAiProviderRepository
{
    Task<IReadOnlyList<AiProvider>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AiProvider>> GetAllAsync(Guid tenantId, CancellationToken ct = default);
    Task<AiProvider?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<AiProvider?> GetByIdAsync(Guid id, Guid tenantId, CancellationToken ct = default);
    Task<AiProvider?> GetByCodeAsync(string code, CancellationToken ct = default);
    Task<AiProvider?> GetByCodeAsync(string code, Guid tenantId, CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetUsedCodesAsync(Guid tenantId, CancellationToken ct = default);
    Task AddAsync(AiProvider provider, CancellationToken ct = default);
    Task UpdateAsync(AiProvider provider, CancellationToken ct = default);
    Task UpdateAsync(AiProvider provider, Guid tenantId, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task DeleteAsync(Guid id, Guid tenantId, CancellationToken ct = default);
}
