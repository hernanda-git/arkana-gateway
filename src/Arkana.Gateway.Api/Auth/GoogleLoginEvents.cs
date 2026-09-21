using System.Security.Claims;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arkana.Gateway.Api.Auth;

/// <summary>
/// Hooks the ASP.NET Google Authentication scheme (Authorization Code + PKCE,
/// <c>GoogleOptions.UsePkce</c>) to enforce the portal's Google login policy
/// and mint the SAME cookie principal shape the dashboard already expects:
///   • NameIdentifier = the DashboardUser row Id (stable identity across sessions)
///   • Name           = email
///   • ClaimTypes.Role = Admin | DefaultRole (config / AdminEmails)
///   • TenantId       = GoogleLoginOptions.DefaultTenantId
///   • LoginProvider  = "Google" (read by GoogleUserScopeMiddleware)
///
/// Registered as a scoped service; Program.cs wires it into
/// <c>GoogleOptions.Events.OnCreatingTicket</c> via the request's
/// <c>RequestServices</c> (so it resolves the repo/db/logger from DI).
///
/// On first login it AUTO-CREATES a DashboardUser row scoped to the default
/// tenant (mirroring AdminUserSeeder). Deliberately does NOT touch
/// IOAuthFlowService / provider OAuth — that is for upstream provider creds.
/// </summary>
public sealed class GoogleLoginEvents
{
    private readonly IDashboardUserRepository _users;
    private readonly GatewayDbContext _db;
    private readonly GoogleLoginOptions _opts;
    private readonly ILogger<GoogleLoginEvents> _log;

    public GoogleLoginEvents(
        IDashboardUserRepository users,
        GatewayDbContext db,
        IOptions<GoogleLoginOptions> opts,
        ILogger<GoogleLoginEvents> log)
    {
        _users = users;
        _db = db;
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>
    /// Invoked by the Google scheme after it has exchanged the code and built a
    /// (Google) identity ticket. We validate, auto-provision the user, and
    /// replace the principal with the dashboard cookie principal.
    /// </summary>
    public async Task OnCreatingTicketAsync(OAuthCreatingTicketContext context)
    {
        var email = context.Principal?.FindFirst(ClaimTypes.Email)?.Value
                    ?? context.Principal?.FindFirst("email")?.Value;

        if (string.IsNullOrWhiteSpace(email))
        {
            _log.LogWarning("Google login rejected: no email claim in ticket.");
            context.Fail("Google account did not return an email address.");
            return;
        }

        // Domain allowlist (empty = public / any Google account).
        var allowed = _opts.ParsedAllowedDomains();
        if (allowed.Count > 0)
        {
            var domain = email.Contains('@') ? email.Split('@')[1].ToLowerInvariant() : string.Empty;
            if (!allowed.Contains(domain))
            {
                _log.LogWarning("Google login rejected: domain '{Domain}' of '{Email}' not allowed.", domain, email);
                context.Fail("Google account domain is not allowed.");
                return;
            }
        }

        var configRole = _opts.ParsedAdminEmails().Contains(email.ToLowerInvariant())
            ? UserRoles.Admin
            : _opts.DefaultRole;

        // Auto-create or resolve the dashboard user (idempotent by email).
        var existing = await _users.GetByUsernameAsync(email);
        DashboardUser user;
        if (existing is null)
        {
            // First-time Google login: seed role from config (AdminEmails / DefaultRole).
            // No password for Google-only users; empty hash is fine — they never
            // use the password form. Tenant = default tenant (seeded via migration).
            user = DashboardUser.Create(
                username: email,
                passwordHash: string.Empty,
                role: configRole,
                email: email);
            user.TenantId = GoogleLoginOptions.DefaultTenantId;
            await _db.DashboardUsers.AddAsync(user);
            await _db.SaveChangesAsync();
            _log.LogInformation("Auto-created Google dashboard user '{Email}' (id={Id}, role={Role}).", email, user.Id, configRole);
        }
        else
        {
            // Subsequent logins: DO NOT overwrite the stored role. The role is
            // managed by the in-UI user-management CRUD (Settings / /admin/users →
            // "Change Role", backed by AuthSvc.UpdateRoleAsync). Re-deriving it
            // from GOOGLE_ADMIN_EMAILS here would clobber any admin/remove-admin
            // action the moment the user logs in again.
            user = existing;
        }

        // Mint the principal from the (now authoritative, stored) role.
        var role = user.Role;

        // Mint the principal the cookie middleware will serialize.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, email),
            new(ClaimTypes.Role, role),
            new("TenantId", user.TenantId.ToString()),
            new("LoginProvider", "Google"),
        };
        var identity = new ClaimsIdentity(claims, GoogleDefaults.AuthenticationScheme);
        context.Principal = new ClaimsPrincipal(identity);
    }
}
