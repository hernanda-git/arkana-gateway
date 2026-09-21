using Arkana.Domain.Entities;
using Microsoft.AspNetCore.Http;

namespace Arkana.Gateway.Api.Middleware;

/// <summary>
/// Enforces dashboard authentication and role-based authorization for the
/// BlazorServer UI page routes.
///
/// Behavior is gated by <c>authEnabled</c> (the same flag that toggles the
/// cookie Authentication/Authorization middleware in Program.cs):
///   • auth disabled  → pass everything through (dashboard is open, as before).
///   • auth enabled   →
///       – unauthenticated request to a protected PAGE route → 302 to /login.
///       – authenticated, but role insufficient for /settings or /admin/users → 403.
///       – the /admin/* API endpoints (api-keys, stats, logs, …) are NOT
///         protected here. They are gated by <see cref="AdminAuthMiddleware"/>
///         instead, which understands the two credentials that surface accepts
///         (a dashboard Admin session or ADMIN_API_KEY).
///
/// NOTE (2026-08-07): this comment previously read "they are intentionally
/// unauthenticated by design and must stay reachable for the admin API
/// bootstrap / liveness probes". That stopped being true when SEC-ARKANA-004 was
/// fixed on 2026-08-06, and the stale wording actively contributed to
/// SEC-ARKANA-006: AdminAuthMiddleware waved browser-looking GETs through on the
/// stated assumption that THIS middleware would gate them, when IsProtectedPage
/// below only ever covered /admin/users.
///
/// The Blazor &lt;AuthorizeRouteView&gt; (NotAuthorized → RedirectToLogin) provides
/// a client-side fallback; this middleware is the server-side enforcement so the
/// redirect also happens without JavaScript.
/// </summary>
public sealed class RoleAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _authEnabled;

    public RoleAuthorizationMiddleware(RequestDelegate next, bool authEnabled)
    {
        _next = next;
        _authEnabled = authEnabled;
    }

    // Routes that require a specific role (prefix -> required role).
    // Only the Admin-only management surfaces are restricted; all other
    // authenticated users may view the operational dashboards.
    private static readonly Dictionary<string, string> RoleRequiredRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/settings"] = UserRoles.Admin,
        ["/admin/users"] = UserRoles.Admin,
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_authEnabled)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "/";

        // Only gate the Blazor page routes. Static assets, the API surface
        // (/v1, /admin/* endpoints) and the login/logout pages stay open.
        if (!IsProtectedPage(path))
        {
            await _next(context);
            return;
        }

        var user = context.User;

        // Unauthenticated → send to login (server-side, so it works without JS).
        if (user.Identity is null || !user.Identity.IsAuthenticated)
        {
            context.Response.Redirect("/login");
            return;
        }

        // Authenticated → enforce role requirements.
        foreach (var (prefix, requiredRole) in RoleRequiredRoutes)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (HasRequiredRole(user, requiredRole)) continue;

            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "forbidden",
                message = $"Role '{userRoleOf(user)}' does not have access to {path}. Required: {requiredRole}"
            });
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// True for the Blazor dashboard PAGE routes that must require a login.
    /// Excludes the /admin/* API endpoints (api-keys, stats, logs, providers) —
    /// those are authenticated by AdminAuthMiddleware, NOT open by design.
    /// Do not treat a route's absence from this list as "unprotected".
    /// </summary>
    private static bool IsProtectedPage(string path) =>
        path == "/" ||
        path.StartsWith("/dashboard") ||
        path.StartsWith("/cost") ||
        path.StartsWith("/api-keys") ||
        path.StartsWith("/providers") ||
        path.StartsWith("/key-pools") ||
        path.StartsWith("/agent-manager") ||
        path.StartsWith("/workflows") ||
        path.StartsWith("/logs") ||
        path.StartsWith("/settings") ||
        path.StartsWith("/portal") ||
        path.StartsWith("/agents") ||
        path.StartsWith("/admin/users");

    private static bool HasRequiredRole(System.Security.Claims.ClaimsPrincipal user, string requiredRole)
    {
        // Admin has access to everything.
        if (user.IsInRole(UserRoles.Admin)) return true;
        return user.IsInRole(requiredRole);
    }

    private static string userRoleOf(System.Security.Claims.ClaimsPrincipal user) =>
        user.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
        ?? user.FindFirst("role")?.Value
        ?? "unknown";
}
