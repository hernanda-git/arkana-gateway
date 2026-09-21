using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Repository for managing webhook subscriptions (Phase 5).
/// </summary>
public interface IWebhookRepository
{
    /// <summary>Returns all webhooks across all tenants.</summary>
    Task<List<Webhook>> GetAllAsync();

    /// <summary>Returns all webhooks for a specific tenant.</summary>
    Task<List<Webhook>> GetByTenantIdAsync(Guid tenantId);

    /// <summary>Returns a webhook by its unique identifier.</summary>
    Task<Webhook?> GetByIdAsync(Guid id);

    /// <summary>Creates a new webhook subscription.</summary>
    Task CreateAsync(Webhook webhook);

    /// <summary>Updates an existing webhook.</summary>
    Task UpdateAsync(Webhook webhook);

    /// <summary>Deletes a webhook by its unique identifier.</summary>
    Task DeleteAsync(Guid id);
}
