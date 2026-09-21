using Microsoft.EntityFrameworkCore;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;

namespace Arkana.Infrastructure.Persistence.Repositories;

public sealed class DashboardUserRepository : IDashboardUserRepository
{
    private readonly GatewayDbContext _db;
    public DashboardUserRepository(GatewayDbContext db) => _db = db;

    public Task<List<DashboardUser>> GetAllAsync()
        => _db.DashboardUsers.OrderBy(u => u.Username).ToListAsync();

    public Task<DashboardUser?> GetByUsernameAsync(string username)
        => _db.DashboardUsers.FirstOrDefaultAsync(u => u.Username == username);

    public Task<DashboardUser?> GetByIdAsync(Guid id)
        => _db.DashboardUsers.FindAsync(id).AsTask();

    public async Task AddAsync(DashboardUser user)
    {
        _db.DashboardUsers.Add(user);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(DashboardUser user)
    {
        _db.DashboardUsers.Update(user);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var user = await _db.DashboardUsers.FindAsync(id);
        if (user is not null)
        {
            _db.DashboardUsers.Remove(user);
            await _db.SaveChangesAsync();
        }
    }
}
