using Microsoft.AspNetCore.Components.Authorization;

namespace Arkana.Gateway.Api.Services;

/// <summary>
/// Resolves the tenant a dashboard circuit reads from.
///
/// Normal path: the authenticated "TenantId" claim minted at login.
///
/// Open-dashboard path (SEC-ARKANA-003: auth disabled via AUTH=0 / Auth:Enabled=false):
/// there is no authenticated principal, so every tenant-scoped dashboard page
/// (Request Logs, provider health) used to fail closed with "Authenticated tenant
/// is required for request logs." and render HTTP 500 for the operator. When the
/// operator has explicitly opened the dashboard, resolve the configured
/// ADMIN_TENANT_ID instead — the tenant the dashboard itself is operated against.
///
/// With auth enabled this class never invents a tenant: unresolved stays
/// unresolved and callers keep failing closed.
/// </summary>
public sealed class DashboardTenantResolver
{
    private readonly Guid? _openDashboardTenantId;

    public DashboardTenantResolver(IConfiguration configuration)
    {
        // Mirrors the authEnabled expression in Program.cs.
        var authEnabled = configuration.GetValue<bool>("Auth:Enabled")
            || string.Equals(configuration["AUTH"], "1", StringComparison.OrdinalIgnoreCase);

        if (!authEnabled
            && Guid.TryParse(configuration["ADMIN_TENANT_ID"], out var tenantId)
            && tenantId != Guid.Empty)
        {
            _openDashboardTenantId = tenantId;
        }
    }

    /// <summary>
    /// Resolves the tenant for a dashboard authentication state.
    /// Returns false when unresolved — callers must then fail closed.
    /// </summary>
    public bool TryResolve(AuthenticationState authState, out Guid tenantId)
    {
        if (authState.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(authState.User.FindFirst("TenantId")?.Value, out tenantId))
        {
            return true;
        }

        if (_openDashboardTenantId is { } openDashboardTenantId)
        {
            tenantId = openDashboardTenantId;
            return true;
        }

        tenantId = default;
        return false;
    }
}
