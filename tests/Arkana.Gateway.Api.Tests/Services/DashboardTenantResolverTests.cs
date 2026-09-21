namespace Arkana.Gateway.Api.Tests.Services;

using System.Security.Claims;
using Arkana.Gateway.Api.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;

public sealed class DashboardTenantResolverTests
{
    private static readonly Guid TenantOne = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantTwo = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static AuthenticationState Anonymous()
        => new(new ClaimsPrincipal(new ClaimsIdentity()));

    private static AuthenticationState Authenticated(string? tenantClaim)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "someone@example.com") };
        if (tenantClaim is not null)
            claims.Add(new Claim("TenantId", tenantClaim));
        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
    }

    [Fact]
    public void TryResolve_uses_the_authenticated_tenant_claim()
    {
        var resolver = new DashboardTenantResolver(Config(("Auth:Enabled", "true"), ("ADMIN_TENANT_ID", TenantTwo.ToString())));

        resolver.TryResolve(Authenticated(TenantOne.ToString()), out var tenantId).Should().BeTrue();

        // The signed-in user's own tenant always wins over the open-dashboard fallback.
        tenantId.Should().Be(TenantOne);
    }

    [Fact]
    public void TryResolve_falls_back_to_the_admin_tenant_when_auth_is_disabled()
    {
        var resolver = new DashboardTenantResolver(Config(("AUTH", "0"), ("ADMIN_TENANT_ID", TenantOne.ToString())));

        resolver.TryResolve(Anonymous(), out var tenantId).Should().BeTrue();

        tenantId.Should().Be(TenantOne);
    }

    [Fact]
    public void TryResolve_fails_closed_for_anonymous_users_when_auth_is_enabled()
    {
        var resolver = new DashboardTenantResolver(Config(("AUTH", "1"), ("ADMIN_TENANT_ID", TenantOne.ToString())));

        resolver.TryResolve(Anonymous(), out _).Should().BeFalse();
    }

    [Fact]
    public void TryResolve_fails_closed_when_auth_is_disabled_but_no_admin_tenant_is_configured()
    {
        var resolver = new DashboardTenantResolver(Config(("AUTH", "0")));

        resolver.TryResolve(Anonymous(), out _).Should().BeFalse();
    }

    [Fact]
    public void TryResolve_treats_a_malformed_claim_as_unresolved()
    {
        var resolver = new DashboardTenantResolver(Config(("AUTH", "0")));

        resolver.TryResolve(Authenticated("not-a-guid"), out _).Should().BeFalse();
    }
}
