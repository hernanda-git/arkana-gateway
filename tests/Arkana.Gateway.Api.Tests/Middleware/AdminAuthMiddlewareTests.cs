namespace Arkana.Gateway.Api.Tests.Middleware;

using System.Net;
using System.Security.Claims;
using Arkana.Domain.Entities;
using Arkana.Gateway.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Regression coverage for SEC-ARKANA-004 — the unauthenticated /admin API.
/// </summary>
public sealed class AdminAuthMiddlewareTests
{
    private const string AdminKey = "admin-secret-key-for-tests";
    private const string AdminTenantId = "00000000-0000-0000-0000-000000000001";

    /// <summary>
    /// Builds a pipeline with AdminAuthMiddleware in front of a trivial
    /// terminal handler. <paramref name="claims"/> simulates a signed-in
    /// dashboard user.
    /// </summary>
    private static IHost BuildHost(
        string? adminKey = AdminKey,
        bool enabled = true,
        params Claim[] claims)
        => BuildHostInEnvironment(Environments.Development, adminKey, enabled, claims);

    private static IHost BuildHostInEnvironment(
        string environment,
        string? adminKey,
        bool enabled,
        params Claim[] claims)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ADMIN_AUTH_ENABLED"] = enabled ? "1" : "0",
            ["ADMIN_TENANT_ID"] = AdminTenantId,
        };
        if (adminKey is not null) settings["ADMIN_API_KEY"] = adminKey;

        return new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .UseEnvironment(environment)
                    .ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(settings))
                    .ConfigureServices(services => services.AddLogging())
                    .Configure(app =>
                    {
                        if (claims.Length > 0)
                        {
                            app.Use(async (ctx, next) =>
                            {
                                ctx.User = new ClaimsPrincipal(
                                    new ClaimsIdentity(claims, "TestAuth"));
                                await next();
                            });
                        }

                        app.UseMiddleware<AdminAuthMiddleware>();
                        app.Run(async ctx => await ctx.Response.WriteAsync("OK"));
                    });
            })
            .Build();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        IHost host, HttpRequestMessage request)
    {
        await host.StartAsync();
        return await host.GetTestClient().SendAsync(request);
    }

    [Fact]
    public async Task AdminApi_WithoutCredential_ShouldReturn401()
    {
        // The core regression: anonymous access to the management API.
        using var host = BuildHost();

        var response = await SendAsync(host,
            new HttpRequestMessage(HttpMethod.Get, "/admin/api-keys"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateApiKey_WithoutCredential_ShouldReturn401()
    {
        // The most dangerous route: it mints a working key and returns it.
        using var host = BuildHost();

        var response = await SendAsync(host,
            new HttpRequestMessage(HttpMethod.Post, "/admin/api-keys"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminApi_WithCorrectAdminKey_ShouldPass()
    {
        using var host = BuildHost();

        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/api-keys");
        request.Headers.Add("X-Admin-Key", AdminKey);

        var response = await SendAsync(host, request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdminApi_WithWrongAdminKey_ShouldReturn401()
    {
        using var host = BuildHost();

        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/api-keys");
        request.Headers.Add("X-Admin-Key", "not-the-right-key");

        var response = await SendAsync(host, request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminApi_WithAdminKeyAsBearer_ShouldPass()
    {
        // Scripts commonly send Authorization: Bearer rather than a custom header.
        using var host = BuildHost();

        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/stats");
        request.Headers.Add("Authorization", $"Bearer {AdminKey}");

        var response = await SendAsync(host, request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdminApi_WithAdminRoleSession_ShouldPass()
    {
        using var host = BuildHost(
            claims: new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, UserRoles.Admin),
            });

        var response = await SendAsync(host,
            new HttpRequestMessage(HttpMethod.Get, "/admin/api-keys"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdminApi_WithNonAdminSession_ShouldReturn401()
    {
        // Authenticated is not sufficient — the role must be Admin.
        using var host = BuildHost(
            claims: new[]
            {
                new Claim(ClaimTypes.Name, "viewer"),
                new Claim(ClaimTypes.Role, "Viewer"),
            });

        var response = await SendAsync(host,
            new HttpRequestMessage(HttpMethod.Get, "/admin/api-keys"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NoAdminKeyConfigured_ShouldFailClosed_NotOpen()
    {
        // A missing secret must never authenticate everyone. This is the
        // failure mode that lets a misconfigured deploy silently reopen the hole.
        using var host = BuildHost(adminKey: null);

        var response = await SendAsync(host,
            new HttpRequestMessage(HttpMethod.Get, "/admin/api-keys"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NonAdminPath_ShouldNotBeGated()
    {
        // /v1 is the tenant surface, guarded by ApiKeyAuthMiddleware instead.
        using var host = BuildHost();

        var response = await SendAsync(host,
            new HttpRequestMessage(HttpMethod.Get, "/v1/models"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BrowserPageRequest_ShouldPassThrough_ForLoginRedirect()
    {
        // /admin/users is a Blazor page. Returning JSON 401 here would break
        // the cookie login flow; RoleAuthorizationMiddleware redirects instead.
        using var host = BuildHost();

        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/users");
        request.Headers.Add("Accept", "text/html,application/xhtml+xml");

        var response = await SendAsync(host, request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdminApi_WithHtmlAcceptHeader_ShouldStillReturn401()
    {
        // SEC-ARKANA-006. IsBrowserPageRequest() waves through ANY GET carrying
        // `Accept: text/html`, on the assumption that such a request can only
        // be a Blazor page under /admin/users which RoleAuthorizationMiddleware
        // will gate instead. It does not: RoleAuthorizationMiddleware's
        // IsProtectedPage list contains only "/admin/users", so every OTHER
        // /admin GET — /admin/api-keys, /admin/logs (prompt + response bodies),
        // /admin/providers, /admin/stats — has no second gate behind it. A
        // one-header curl (`curl -H 'Accept: text/html' /admin/api-keys`) reads
        // the whole management surface anonymously. The pass-through must be
        // scoped to the page routes that actually have a page gate.
        using var host = BuildHost();

        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/api-keys");
        request.Headers.Add("Accept", "text/html");

        var response = await SendAsync(host, request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminLogs_WithHtmlAcceptHeader_ShouldStillReturn401()
    {
        // /admin/logs returns recorded prompt and response content — the most
        // sensitive read on the surface.
        using var host = BuildHost();

        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/logs");
        request.Headers.Add("Accept", "text/html,application/xhtml+xml,*/*");

        var response = await SendAsync(host, request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DisabledViaConfig_ShouldRestoreOpenBehaviour()
    {
        // Documented escape hatch for migrating existing automation.
        using var host = BuildHost(enabled: false);

        var response = await SendAsync(host,
            new HttpRequestMessage(HttpMethod.Get, "/admin/api-keys"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DisabledViaConfig_InStaging_ShouldRemainUnauthorized()
    {
        using var host = BuildHostInEnvironment(Environments.Staging, AdminKey, enabled: false);

        var response = await SendAsync(host,
            new HttpRequestMessage(HttpMethod.Get, "/admin/api-keys"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
