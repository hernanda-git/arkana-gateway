using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Repository for managing AI models per provider.
/// </summary>
public interface IModelRepository
{
    Task<IReadOnlyList<Model>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Model>> GetAllAsync(Guid tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<Model>> GetByProviderIdAsync(Guid providerId, CancellationToken ct = default);
    Task<IReadOnlyList<Model>> GetByProviderIdAsync(Guid providerId, Guid tenantId, CancellationToken ct = default);
    Task<Model?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Model?> GetByIdAsync(Guid id, Guid tenantId, CancellationToken ct = default);
    /// <summary>Look up a model by its unique code (e.g., "deepseek-v4-flash").</summary>
    Task<Model?> GetByCodeAsync(string code, CancellationToken ct = default);
    Task<Model?> GetByCodeAsync(string code, Guid tenantId, CancellationToken ct = default);
    Task AddAsync(Model model, CancellationToken ct = default);
    Task UpdateAsync(Model model, CancellationToken ct = default);
    Task UpdateAsync(Model model, Guid tenantId, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task DeleteAsync(Guid id, Guid tenantId, CancellationToken ct = default);
}
