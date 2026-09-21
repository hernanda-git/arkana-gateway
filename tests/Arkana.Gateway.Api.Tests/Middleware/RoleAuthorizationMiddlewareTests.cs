namespace Arkana.Gateway.Api.Tests.Middleware;

using System.Net;
using System.Security.Claims;
using Arkana.Domain.Entities;
using Arkana.Gateway.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

public sealed class RoleAuthorizationMiddlewareTests
{
    private static ClaimsPrincipal MakeUser(string role) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, role) }, "test"));

    private static IHost BuildHost(bool authEnabled, ClaimsPrincipal? user = null)
    {
        return new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services => { })
                    .Configure(app =>
                    {
                        // Simulate the cookie Authentication middleware having
                        // established a ClaimsPrincipal on the request.
                        app.Use(async (context, next) =>
                        {
                            if (user is not null) context.User = user;
                            await next();
                        });
                        app.UseMiddleware<RoleAuthorizationMiddleware>(authEnabled);
                        app.Run(async ctx =>
                        {
                            ctx.Response.StatusCode = 200;
                            await ctx.Response.WriteAsync("OK");
                        });
                    });
            })
            .Build();
    }

    // ── Auth DISABLED: everything passes through (dashboard is open) ──

    [Fact]
    public async Task AuthDisabled_ProtectedPagePassesThrough()
    {
        using var host = BuildHost(authEnabled: false);
        await host.StartAsync();
        var client = host.GetTestClient();

        (await client.GetAsync("/dashboard")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/settings")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/admin/api-keys")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Auth ENABLED: unauthenticated users are redirected to /login ──

    [Fact]
    public async Task AuthEnabled_Unauthenticated_ProtectedPageRedirectsToLogin()
    {
        using var host = BuildHost(authEnabled: true);
        await host.StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/dashboard");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/login");
    }

    [Fact]
    public async Task AuthEnabled_Unauthenticated_OpenAdminApiPassesThrough()
    {
        using var host = BuildHost(authEnabled: true);
        await host.StartAsync();
        var client = host.GetTestClient();

        // /admin/* API endpoints are intentionally open (unauthenticated by design).
        (await client.GetAsync("/admin/api-keys")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Auth ENABLED + authenticated: role checks ──

    [Fact]
    public async Task AuthEnabled_Admin_CanAccessSettings()
    {
        using var host = BuildHost(authEnabled: true, user: MakeUser(UserRoles.Admin));
        await host.StartAsync();
        var client = host.GetTestClient();

        (await client.GetAsync("/settings")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AuthEnabled_NonAdmin_CannotAccessSettings_Returns403()
    {
        using var host = BuildHost(authEnabled: true, user: MakeUser(UserRoles.User));
        await host.StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/settings");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("forbidden");
    }

    [Fact]
    public async Task AuthEnabled_Admin_CanAccessAdminUsers()
    {
        using var host = BuildHost(authEnabled: true, user: MakeUser(UserRoles.Admin));
        await host.StartAsync();
        var client = host.GetTestClient();

        (await client.GetAsync("/admin/users")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AuthEnabled_NonAdmin_CannotAccessAdminUsers_Returns403()
    {
        using var host = BuildHost(authEnabled: true, user: MakeUser(UserRoles.User));
        await host.StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/admin/users");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("forbidden");
    }

    [Fact]
    public async Task AuthEnabled_AuthenticatedUser_CanAccessOperationalDashboards()
    {
        using var host = BuildHost(authEnabled: true, user: MakeUser(UserRoles.User));
        await host.StartAsync();
        var client = host.GetTestClient();

        (await client.GetAsync("/dashboard")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/cost")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/providers")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Paths that must never be gated ──

    [Fact]
    public async Task ApiPathsAreSkipped_V1NotChecked()
    {
        using var host = BuildHost(authEnabled: true);
        await host.StartAsync();
        var client = host.GetTestClient();

        (await client.GetAsync("/v1/chat/completions")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NonDashboardPathsAreSkipped()
    {
        using var host = BuildHost(authEnabled: true);
        await host.StartAsync();
        var client = host.GetTestClient();

        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
