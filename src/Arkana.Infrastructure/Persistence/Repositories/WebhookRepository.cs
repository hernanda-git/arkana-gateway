using Microsoft.EntityFrameworkCore;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;

namespace Arkana.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IWebhookRepository"/> for managing
/// webhook subscriptions in PostgreSQL.
/// </summary>
public sealed class WebhookRepository : IWebhookRepository
{
    private readonly GatewayDbContext _ctx;

    public WebhookRepository(GatewayDbContext ctx) => _ctx = ctx;

    public Task<List<Webhook>> GetAllAsync()
        => _ctx.Webhooks
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync();

    public Task<List<Webhook>> GetByTenantIdAsync(Guid tenantId)
        => _ctx.Webhooks
            .Where(w => w.TenantId == tenantId)
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync();

    public Task<Webhook?> GetByIdAsync(Guid id)
        => _ctx.Webhooks.FindAsync(id).AsTask();

    public async Task CreateAsync(Webhook webhook)
    {
        _ctx.Webhooks.Add(webhook);
        await _ctx.SaveChangesAsync();
    }

    public async Task UpdateAsync(Webhook webhook)
    {
        _ctx.Webhooks.Update(webhook);
        await _ctx.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var webhook = await _ctx.Webhooks.FindAsync(id);
        if (webhook is not null)
        {
            _ctx.Webhooks.Remove(webhook);
            await _ctx.SaveChangesAsync();
        }
    }
}
