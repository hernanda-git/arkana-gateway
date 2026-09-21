using System.Security.Claims;
using Arkana.Domain.Entities;
using Arkana.Gateway.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Arkana.Gateway.Api.Tests.Middleware;

public sealed class OAuthManagementAuthorizationTests
{
    [Fact]
    public async Task NonAdminAuthenticatedUserCannotUseOAuthManagementPolicy()
    {
        using var services = BuildServices();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var viewer = Principal("Viewer");

        var result = await authorization.AuthorizeAsync(viewer, null, "OAuthManagement");

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task AdminAuthenticatedUserCanUseOAuthManagementPolicy()
    {
        using var services = BuildServices();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var admin = Principal(UserRoles.Admin);

        var result = await authorization.AuthorizeAsync(admin, null, "OAuthManagement");

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task AnonymousUserCannotUseOAuthManagementPolicy()
    {
        using var services = BuildServices();
        var authorization = services.GetRequiredService<IAuthorizationService>();

        var result = await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, "OAuthManagement");

        result.Succeeded.Should().BeFalse();
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(GatewayAuthorizationPolicyRegistration.AddOAuthManagement);
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Principal(string role)
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "test-user"),
                new Claim(ClaimTypes.Role, role)
            ],
            authenticationType: "test"));
}
