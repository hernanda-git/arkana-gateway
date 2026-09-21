using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Persistence.Repositories;

public sealed class PolicyTemplateRepository : IPolicyTemplateRepository
{
    private readonly GatewayDbContext _ctx;

    public PolicyTemplateRepository(GatewayDbContext ctx) => _ctx = ctx;

    public async Task<List<PolicyTemplate>> GetAllAsync()
        => await _ctx.PolicyTemplates
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .ToListAsync();

    public async Task<PolicyTemplate?> GetBySlugAsync(string slug)
        => await _ctx.PolicyTemplates
            .FirstOrDefaultAsync(t => t.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));

    public async Task<PolicyTemplate?> GetByIdAsync(Guid id)
        => await _ctx.PolicyTemplates.FindAsync(id);

    public async Task AddAsync(PolicyTemplate policyTemplate)
    {
        _ctx.PolicyTemplates.Add(policyTemplate);
        await _ctx.SaveChangesAsync();
    }

    public async Task UpdateAsync(PolicyTemplate policyTemplate)
    {
        _ctx.PolicyTemplates.Update(policyTemplate);
        await _ctx.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var template = await _ctx.PolicyTemplates.FindAsync(id);
        if (template is not null)
        {
            template.Deactivate();
            await _ctx.SaveChangesAsync();
        }
    }
}
