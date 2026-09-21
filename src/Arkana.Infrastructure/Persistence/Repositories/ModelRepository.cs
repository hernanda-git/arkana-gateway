using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Persistence.Repositories;

internal sealed class ModelRepository : IModelRepository
{
    private readonly GatewayDbContext _db;

    public ModelRepository(GatewayDbContext db) => _db = db;

    public async Task<IReadOnlyList<Model>> GetAllAsync(CancellationToken ct)
        => await _db.Models.Include(m => m.Provider)
            .OrderByDescending(m => m.IsEnabled)
            .ThenBy(m => m.Provider.Code)
            .ThenBy(m => m.Name)
            .ThenBy(m => m.Code)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Model>> GetAllAsync(Guid tenantId, CancellationToken ct)
        => await _db.Models.Include(m => m.Provider)
            .Where(m => m.Provider.TenantId == tenantId)
            .OrderByDescending(m => m.IsEnabled)
            .ThenBy(m => m.Provider.Code)
            .ThenBy(m => m.Name)
            .ThenBy(m => m.Code)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Model>> GetByProviderIdAsync(Guid providerId, CancellationToken ct)
        => await _db.Models.Where(m => m.ProviderId == providerId).OrderBy(m => m.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<Model>> GetByProviderIdAsync(Guid providerId, Guid tenantId, CancellationToken ct)
        => await _db.Models.Where(m => m.ProviderId == providerId && m.Provider.TenantId == tenantId)
            .OrderBy(m => m.Name).ToListAsync(ct);

    public async Task<Model?> GetByIdAsync(Guid id, CancellationToken ct)
        => await _db.Models.Include(m => m.Provider).FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<Model?> GetByIdAsync(Guid id, Guid tenantId, CancellationToken ct)
        => await _db.Models.Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == id && m.Provider.TenantId == tenantId, ct);

    public async Task<Model?> GetByCodeAsync(string code, CancellationToken ct)
        => await _db.Models.Include(m => m.Provider)
            .Where(m => m.Code == code)
            .OrderByDescending(m => m.IsEnabled)
            .ThenBy(m => m.Provider.Code)
            .ThenBy(m => m.Name)
            .FirstOrDefaultAsync(ct);

    public async Task<Model?> GetByCodeAsync(string code, Guid tenantId, CancellationToken ct)
        => await _db.Models.Include(m => m.Provider)
            .Where(m => m.Code == code && m.Provider.TenantId == tenantId)
            .OrderByDescending(m => m.IsEnabled)
            .ThenBy(m => m.Provider.Code)
            .ThenBy(m => m.Name)
            .FirstOrDefaultAsync(ct);

    public async Task AddAsync(Model model, CancellationToken ct)
    {
        _db.Models.Add(model);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Model model, CancellationToken ct)
    {
        _db.Models.Update(model);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Model model, Guid tenantId, CancellationToken ct)
    {
        var existing = await _db.Models
            .Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == model.Id && m.Provider.TenantId == tenantId, ct);
        if (existing is null)
            return;

        _db.Entry(existing).CurrentValues.SetValues(model);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var model = await _db.Models.FindAsync([id], ct);
        if (model is not null)
        {
            _db.Models.Remove(model);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteAsync(Guid id, Guid tenantId, CancellationToken ct)
    {
        var model = await _db.Models
            .Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == id && m.Provider.TenantId == tenantId, ct);
        if (model is not null)
        {
            _db.Models.Remove(model);
            await _db.SaveChangesAsync(ct);
        }
    }
}
