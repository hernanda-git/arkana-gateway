using Arkana.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Arkana.Gateway.Api.Middleware;

public sealed class AdminApiRequirement : IAuthorizationRequirement
{
}

public static class GatewayAuthorizationPolicyRegistration
{
    public static void AddOAuthManagement(AuthorizationOptions options)
    {
        options.AddPolicy("OAuthManagement", policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireRole(UserRoles.Admin);
        });
    }
}

/// <summary>
/// Authorizes management API calls for either a dashboard Admin session or the
/// separately configured X-Admin-Key/Authorization bearer secret.
/// </summary>
public sealed class AdminApiAuthorizationHandler : AuthorizationHandler<AdminApiRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;

    public AdminApiAuthorizationHandler(
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminApiRequirement requirement)
    {
        var user = context.User;
        var isAdminSession = user.Identity?.IsAuthenticated == true
            && (user.IsInRole(UserRoles.Admin)
                || user.Claims.Any(c =>
                    c.Type == System.Security.Claims.ClaimTypes.Role
                    && string.Equals(c.Value, UserRoles.Admin, StringComparison.OrdinalIgnoreCase)));

        var request = _httpContextAccessor.HttpContext?.Request;
        if (isAdminSession
            || (request is not null
                && AdminAuthMiddleware.HasValidConfiguredAdminKey(
                    request, _configuration["ADMIN_API_KEY"])))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
