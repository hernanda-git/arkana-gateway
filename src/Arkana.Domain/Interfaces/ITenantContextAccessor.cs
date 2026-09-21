namespace Arkana.Domain.Interfaces;

/// <summary>
/// Supplies the authenticated dashboard tenant to a long-lived Blazor circuit.
/// HTTP request context is not available during later circuit events, so the
/// dashboard initializes this value from its authentication claims once the
/// circuit is established.
/// </summary>
public interface ITenantContextAccessor
{
    /// <summary>
    /// Pins the circuit to a non-empty authenticated tenant identifier.
    /// </summary>
    void SetTenant(Guid tenantId);

    /// <summary>
    /// Clears the tenant when the circuit's authenticated identity is removed.
    /// </summary>
    void ClearTenant();
}
