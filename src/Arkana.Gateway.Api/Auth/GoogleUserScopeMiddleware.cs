using System.Text.RegularExpressions;
using Arkana.Domain.Entities;
using Microsoft.AspNetCore.Http;

namespace Arkana.Gateway.Api.Auth;

/// <summary>
/// Enforces the "single-page UI" rule for Google-login dashboard users: once a
/// user is signed in via Google (carries the <c>LoginProvider=Google</c> claim),
/// they are allowed ONLY the chrome-less <see cref="Components.Pages.Profile"/>
/// page (and /logout). Any other navigation is redirected to /profile.
///
/// This is a UX/scope guard layered ON TOP of RoleAuthorizationMiddleware (which
/// still gates /admin and /settings on Admin). It deliberately does NOT touch
/// API-key or admin-key auth — those requests never carry the Google cookie
/// principal, so they fall straight through.
///
/// Exemptions (so the profile page actually renders and the OAuth callback works):
///   • static assets (.css/.js/.png/.svg/.woff2 etc. and /_framework, /css, /js,
///     /lib, /_content prefixes) — needed by the Blazor profile page;
///   • the Google callback path + the login trigger (handled by the auth handler
///     itself, which short-circuits before this middleware runs);
///   • /profile* and /logout (explicitly allowed).
/// </summary>
public sealed class GoogleUserScopeMiddleware
{
    private readonly RequestDelegate _next;

    // Paths the auth handler owns — it short-circuits these, but exempt them so we
    // never interfere even if middleware order changes.
    private static readonly HashSet<string> s_exemptPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/signin-google",
        "/auth/google/login",
        "/logout",
    };

    // Prefixes for framework/static assets the profile page depends on.
    private static readonly string[] s_assetPrefixes =
    {
        "/_framework/", "/css/", "/js/", "/lib/", "/_content/", "/_blazor/",
    };

    private static readonly Regex s_staticExt = new(
        @"\.(css|js|mjs|ts|json|map|png|jpg|jpeg|gif|svg|ico|woff2?|ttf|eot|webmanifest)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public GoogleUserScopeMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";

        // Only non-Admin Google users are confined to the chrome-less /profile.
        // An elevated (Admin) Google user keeps full access to the dashboard nav
        // and all menus — the NavMenu's <AuthorizeView Roles="Admin"> already
        // gates the admin-only links for them.
        if (context.User.IsGoogleLoginUser()
            && !context.User.IsInRole(UserRoles.Admin)
            && !IsExempt(path))
        {
            context.Response.Redirect("/profile");
            return;
        }

        await _next(context);
    }

    private static bool IsExempt(string path)
    {
        if (path.StartsWith("/profile", StringComparison.OrdinalIgnoreCase))
            return true;
        // Google users may read the public Knowledge Base/docs without being
        // promoted into the full dashboard surface.
        if (path.StartsWith("/knowledge-base", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/kb", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/docs", StringComparison.OrdinalIgnoreCase))
            return true;
        if (s_exemptPaths.Contains(path))
            return true;
        // API surfaces are owned by ApiKeyAuthMiddleware / AdminAuthMiddleware and
        // never carry the Google cookie principal — never redirect them.
        if (path.StartsWith("/v1", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/oauth", StringComparison.OrdinalIgnoreCase))
            return true;
        if (s_assetPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (s_staticExt.IsMatch(path))
            return true;
        return false;
    }
}
