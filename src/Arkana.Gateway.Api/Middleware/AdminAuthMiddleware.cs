using Arkana.Domain.Entities;
using Microsoft.AspNetCore.Http;

namespace Arkana.Gateway.Api.Middleware;

/// <summary>
/// Authenticates the <c>/admin/*</c> management API (SEC-ARKANA-004).
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-08-06 <see cref="ApiKeyAuthMiddleware"/> skipped every path under
/// <c>/admin</c> with the comment "Phase 1 — secured in later phases", and
/// <see cref="RoleAuthorizationMiddleware"/> deliberately left the same surface
/// open. The result was that the whole management API was reachable with no
/// credential at all, including <c>POST /admin/api-keys</c>, which mints a
/// working gateway key and returns it in plaintext. That single gap defeated
/// every other control in the system — rate limiting, budgets, model
/// allow-lists and metering are all keyed off an API key that an anonymous
/// caller could issue for themselves. It also exposed
/// <c>PUT /admin/providers/{id}/apikey</c> (overwrite upstream credentials) and
/// <c>GET /admin/logs</c> (prompt and response content).
/// </para>
/// <para>
/// Two credentials are accepted, because the surface has two legitimate kinds
/// of caller:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>An admin dashboard session.</b> A signed-in <see cref="DashboardUser"/>
///     holding the <see cref="UserRoles.Admin"/> role. This is how the Blazor UI
///     reaches the API when a human is driving it.
///   </description></item>
///   <item><description>
///     <b>The admin API key.</b> Supplied as <c>X-Admin-Key</c> (or
///     <c>Authorization: Bearer</c>) and compared against
///     <c>ADMIN_API_KEY</c>. This is for scripts, provisioning and smoke tests,
///     which have no cookie jar.
///   </description></item>
/// </list>
/// <para>
/// The comparison is fixed-time (<see cref="FixedTimeEquals"/>) so a caller
/// cannot recover the key one byte at a time by measuring response latency.
/// </para>
/// <para>
/// <b>Failure mode is deliberate.</b> When enforcement is enabled but no
/// <c>ADMIN_API_KEY</c> is configured, key-based auth is treated as unavailable
/// and only a dashboard Admin session is accepted — it never falls back to
/// "allow everyone". A missing secret must fail closed; the opposite is how the
/// original hole persisted for so long.
/// </para>
/// <para>
/// Set <c>ADMIN_AUTH_ENABLED=0</c> only in Development to restore the old open
/// behaviour for local automation. Non-development environments ignore that
/// escape hatch and remain fail-closed.
/// </para>
/// </remarks>
public sealed class AdminAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AdminAuthMiddleware> _logger;
    private readonly bool _enabled;
    private readonly string? _adminKey;
    private readonly Guid? _adminTenantId;

    public AdminAuthMiddleware(
        RequestDelegate next,
        ILogger<AdminAuthMiddleware> logger,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;

        // Default ON. The legacy disable switch is deliberately limited to local
        // Development so a staging/production misconfiguration cannot open the
        // management API.
        var disableRequested = string.Equals(
            configuration["ADMIN_AUTH_ENABLED"], "0", StringComparison.Ordinal);
        _enabled = !disableRequested || !environment.IsDevelopment();

        var key = configuration["ADMIN_API_KEY"];
        _adminKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        var tenantText = configuration["ADMIN_TENANT_ID"];
        _adminTenantId = Guid.TryParse(tenantText, out var tenantId) && tenantId != Guid.Empty
            ? tenantId
            : null;

        if (!_enabled)
        {
            _logger.LogWarning(
                "ADMIN_AUTH_ENABLED=0 — the /admin management API is UNAUTHENTICATED. " +
                "Anyone who can reach this gateway can mint API keys and overwrite " +
                "provider credentials. Set ADMIN_AUTH_ENABLED=1 as soon as any " +
                "remaining automation has been migrated to ADMIN_API_KEY.");
        }
        else if (_adminKey is null)
        {
            _logger.LogWarning(
                "ADMIN_API_KEY is not configured. The /admin API is reachable only " +
                "with a signed-in dashboard Admin session; scripted access will 401.");
        }
        else if (_adminTenantId is null)
        {
            _logger.LogWarning(
                "ADMIN_TENANT_ID is not configured. Admin API-key requests will be rejected "
                + "because tenant scope cannot be established.");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enabled || !context.Request.Path.StartsWithSegments("/admin"))
        {
            await _next(context);
            return;
        }

        // The Blazor management PAGES (/admin/users) live under the same prefix
        // as the API. They are already gated by RoleAuthorizationMiddleware,
        // which redirects a browser to /login rather than returning a bare 401.
        // Answering those with JSON here would break the login flow.
        if (IsBrowserPageRequest(context.Request))
        {
            await _next(context);
            return;
        }

        if (HasAdminSession(context))
        {
            await _next(context);
            return;
        }

        if (HasValidAdminKey(context.Request))
        {
            if (_adminTenantId is not { } tenantId)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        message = "Admin tenant scope is not configured.",
                        type = "admin_tenant_not_configured",
                        code = 503
                    }
                });
                return;
            }

            // Bind API-key based administrative operations to one explicit
            // tenant. Cookie sessions carry their own TenantId claim.
            context.Items["TenantId"] = tenantId;
            await _next(context);
            return;
        }

        _logger.LogWarning(
            "Rejected unauthenticated {Method} {Path} on the admin API from {Ip}",
            context.Request.Method, context.Request.Path,
            context.Connection.RemoteIpAddress);

        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message = "Admin authentication required. Supply the X-Admin-Key "
                        + "header or sign in to the dashboard as an Admin.",
                type = "admin_auth_required",
                code = 401
            }
        });
    }

    /// <summary>
    /// True when the caller holds an authenticated dashboard session carrying
    /// the Admin role.
    /// </summary>
    private static bool HasAdminSession(HttpContext context)
    {
        var user = context.User;
        if (user.Identity is null || !user.Identity.IsAuthenticated) return false;

        return user.IsInRole(UserRoles.Admin)
            || user.Claims.Any(c =>
                   c.Type == System.Security.Claims.ClaimTypes.Role
                   && string.Equals(c.Value, UserRoles.Admin, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when the request carries the configured admin API key. Returns
    /// false when no key is configured — absence of a secret must never
    /// authenticate anyone.
    /// </summary>
    private bool HasValidAdminKey(HttpRequest request)
        => HasValidConfiguredAdminKey(request, _adminKey);

    internal static bool HasValidConfiguredAdminKey(HttpRequest request, string? expectedKey)
    {
        if (string.IsNullOrEmpty(expectedKey)) return false;

        var presented = request.Headers["X-Admin-Key"].FirstOrDefault();

        if (string.IsNullOrEmpty(presented)
            && ApiKeyExtractor.TryExtractBearer(request, out var bearer))
        {
            presented = bearer;
        }

        return !string.IsNullOrEmpty(presented) && FixedTimeEquals(presented, expectedKey);
    }

    /// <summary>
    /// Length-independent, fixed-time string comparison. A plain <c>==</c>
    /// short-circuits on the first differing byte, which leaks the shared
    /// prefix length through response timing.
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var ab = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    /// <summary>
    /// The Blazor dashboard PAGE routes that live under <c>/admin</c>. This is
    /// an exhaustive allow-list, derived from the <c>@page</c> directives in
    /// <c>Components/Pages/Admin/</c>, and it must stay in sync with them.
    /// </summary>
    /// <remarks>
    /// SEC-ARKANA-006. This used to be a content-negotiation check: any GET whose
    /// <c>Accept</c> header contained <c>text/html</c> was waved through as
    /// "a browser navigating to a page". An <c>Accept</c> header is caller-
    /// controlled, and the assumed second gate did not exist — the only
    /// <c>/admin</c> prefix in <c>RoleAuthorizationMiddleware.IsProtectedPage</c>
    /// is <c>/admin/users</c>. So <c>curl -H 'Accept: text/html'
    /// /admin/api-keys</c> (or <c>/admin/logs</c>, which returns recorded
    /// prompt and response bodies) read the entire management API with no
    /// credential. Matching the route instead of the header closes that: an
    /// API path is now gated whatever the caller claims to accept.
    /// </remarks>
    private static readonly string[] AdminPageRoutes =
    [
        "/admin/users",
        "/admin/policy-templates",
    ];

    /// <summary>
    /// Distinguishes a browser navigating to a Blazor admin page from an API
    /// client. Matches on the ROUTE (page routes are a fixed, known set), not
    /// on the caller-supplied <c>Accept</c> header.
    /// </summary>
    private static bool IsBrowserPageRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method)) return false;

        var accept = request.Headers.Accept.ToString();
        if (!accept.Contains("text/html", StringComparison.OrdinalIgnoreCase)) return false;

        return AdminPageRoutes.Any(route =>
            request.Path.StartsWithSegments(route, StringComparison.OrdinalIgnoreCase));
    }
}
