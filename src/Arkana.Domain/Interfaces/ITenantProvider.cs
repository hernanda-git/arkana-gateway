namespace Arkana.Domain.Interfaces;

/// <summary>
/// Resolves the current tenant for request-scoped data isolation
/// (ENT-ARKANA-001). The implementation reads from the authenticated
/// API key or dashboard user session and is registered as a scoped
/// service so each request gets its own resolution.
/// </summary>
public interface ITenantProvider
{
    /// <summary>
    /// The current tenant's ID. Returns <c>null</c> for the global/admin
    /// tenant (super-admin operations, unauthenticated health checks).
    /// </summary>
    Guid? TenantId { get; }

    /// <summary>
    /// True when a specific tenant is resolved (non-null TenantId).
    /// </summary>
    bool HasTenant => TenantId.HasValue;
}
