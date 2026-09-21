using Arkana.Domain.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// Resolves the current tenant from <see cref="HttpContext.Items"/> (set by
/// <see cref="Arkana.Gateway.Api.Middleware.ApiKeyAuthMiddleware"/> for
/// API-key-authenticated requests) or from the "TenantId" claim (set by
/// the dashboard login page for cookie-authenticated sessions).
///
/// Returns <c>null</c> when no specific tenant is resolved. Callers that
/// persist or read tenant-scoped data must fail closed in that case.
/// </summary>
public sealed class HttpContextTenantProvider : ITenantProvider, ITenantContextAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private Guid? _circuitTenantId;

    public HttpContextTenantProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? TenantId
    {
        get
        {
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx is null) return _circuitTenantId;

            // 1) API key auth — middleware stores TenantId in Items
            if (ctx.Items.TryGetValue("TenantId", out var tenantObj) && tenantObj is Guid tenantId)
                return tenantId;

            // 2) Dashboard user auth — stored in claims on login
            var claim = ctx.User.FindFirst("TenantId")?.Value;
            if (claim is not null && Guid.TryParse(claim, out var claimTenantId))
                return claimTenantId;

            // Blazor Server circuit events do not carry an HttpContext. The
            // dashboard pins the authenticated claim into this scoped provider
            // once the circuit is established, preserving fail-closed behavior
            // for ordinary HTTP/background scopes that never set a circuit tenant.
            return _circuitTenantId;
        }
    }

    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));

        _circuitTenantId = tenantId;
    }

    public void ClearTenant()
        => _circuitTenantId = null;
}
