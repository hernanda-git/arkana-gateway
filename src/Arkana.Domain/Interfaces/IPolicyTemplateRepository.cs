using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Repository for compliance policy templates.
/// </summary>
public interface IPolicyTemplateRepository
{
    Task<List<PolicyTemplate>> GetAllAsync();
    Task<PolicyTemplate?> GetBySlugAsync(string slug);
    Task<PolicyTemplate?> GetByIdAsync(Guid id);
    Task AddAsync(PolicyTemplate policyTemplate);
    Task UpdateAsync(PolicyTemplate policyTemplate);
    Task DeleteAsync(Guid id);
}
