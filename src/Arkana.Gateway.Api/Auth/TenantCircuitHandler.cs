using Arkana.Domain.Interfaces;
using Arkana.Gateway.Api.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Arkana.Gateway.Api.Auth;

/// <summary>
/// Pins the dashboard tenant into each Blazor circuit's DI scope when the
/// circuit opens.
///
/// Why the layout is not enough: <c>App.razor</c> renders &lt;Routes /&gt; without a
/// render mode and each page declares <c>@rendermode InteractiveServer</c>, so
/// MainLayout lives in the statically rendered part of the tree. Its
/// <c>OnInitializedAsync</c> therefore runs during the HTTP prerender (where it
/// pins the tenant for that request scope) but never inside the interactive
/// circuit, which gets a fresh DI scope with no tenant. Tenant-scoped reads then
/// fail with "Authenticated tenant is required for request logs." inside the
/// circuit — and the pages' resilient catches turn that into panels stuck on
/// "Loading..." instead of an error.
///
/// This handler runs before any component in the circuit initializes, so the
/// scoped <see cref="ITenantProvider"/> every dashboard service resolves already
/// carries the tenant. MainLayout keeps its pin as the prerender-path fallback.
/// </summary>
public sealed class TenantCircuitHandler : CircuitHandler
{
    private readonly AuthenticationStateProvider _authState;
    private readonly DashboardTenantResolver _resolver;
    private readonly ITenantContextAccessor _tenant;
    private readonly ILogger<TenantCircuitHandler> _log;

    public TenantCircuitHandler(
        AuthenticationStateProvider authState,
        DashboardTenantResolver resolver,
        ITenantContextAccessor tenant,
        ILogger<TenantCircuitHandler> log)
    {
        _authState = authState;
        _resolver = resolver;
        _tenant = tenant;
        _log = log;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
        => PinTenantAsync(circuit.Id, cancellationToken);

    internal async Task PinTenantAsync(string circuitId, CancellationToken ct = default)
    {
        try
        {
            var authState = await _authState.GetAuthenticationStateAsync();
            if (_resolver.TryResolve(authState, out var tenantId))
            {
                _tenant.SetTenant(tenantId);
            }
        }
        catch (Exception ex)
        {
            // Circuits must still come up; the layout pin and the pages' tenant
            // guards keep the failure contained and visible.
            _log.LogWarning(ex, "Unable to resolve the dashboard tenant for circuit {CircuitId}.", circuitId);
        }
    }
}
