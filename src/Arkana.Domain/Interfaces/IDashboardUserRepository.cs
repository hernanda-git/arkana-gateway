using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

public interface IDashboardUserRepository
{
    Task<List<DashboardUser>> GetAllAsync();
    Task<DashboardUser?> GetByUsernameAsync(string username);
    Task<DashboardUser?> GetByIdAsync(Guid id);
    Task AddAsync(DashboardUser user);
    Task UpdateAsync(DashboardUser user);
    Task DeleteAsync(Guid id);
}
