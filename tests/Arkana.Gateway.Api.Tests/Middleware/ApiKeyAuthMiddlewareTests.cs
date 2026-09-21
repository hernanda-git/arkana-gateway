namespace Arkana.Gateway.Api.Tests.Middleware;

using System.Net;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Gateway.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

public sealed class ApiKeyAuthMiddlewareTests
{
    private static (string PlainKey, ApiKey ApiKey) CreateTestKey(string name = "test-key")
    {
        var plainKey = ApiKeyHasher.GenerateApiKey();
        var hash = ApiKeyHasher.Hash(plainKey);
        var key = ApiKey.Create(name, hash, ApiKeyHasher.ExtractPrefix(plainKey));
        return (plainKey, key);
    }

    private static IHost BuildHost(IApiKeyRepository repo)
    {
        return new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(repo);
                    })
                    .Configure(app =>
                    {
                        app.UseMiddleware<ApiKeyAuthMiddleware>();
                        app.Run(async ctx =>
                        {
                            ctx.Response.StatusCode = 200;
                            await ctx.Response.WriteAsync("OK");
                        });
                    });
            })
            .Build();
    }

    [Fact]
    public async Task HealthEndpoint_ShouldPassThrough_WithoutApiKey()
    {
        var repo = Substitute.For<IApiKeyRepository>();
        using var host = BuildHost(repo);
        await host.StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("OK");
    }

    [Fact]
    public async Task AdminEndpoint_ShouldDeferToAdminAuthMiddleware_NotValidateTenantKey()
    {
        // ApiKeyAuthMiddleware validates TENANT api keys. Admin callers present
        // a different credential (a dashboard Admin session or ADMIN_API_KEY),
        // so this middleware must pass /admin through rather than 401 it.
        //
        // This test previously asserted the same pass-through under the name
        // "AdminEndpoint_ShouldPassThrough_WithoutApiKey", which read as
        // "the admin API is intentionally open" and pinned SEC-ARKANA-004 — the
        // unauthenticated /admin hole — as expected behaviour. The pass-through
        // here is correct; the actual gate is AdminAuthMiddleware, covered by
        // AdminAuthMiddlewareTests.
        var repo = Substitute.For<IApiKeyRepository>();
        using var host = BuildHost(repo);
        await host.StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/admin/api-keys");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // And it must NOT have consulted the tenant key repository.
        await repo.DidNotReceiveWithAnyArgs()
            .GetByKeyHashAsync(default!, default);
    }

    [Fact]
    public async Task MissingApiKey_ShouldReturn401()
    {
        var repo = Substitute.For<IApiKeyRepository>();
        using var host = BuildHost(repo);
        await host.StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/v1/chat/completions");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InvalidApiKey_ShouldReturn401()
    {
        var repo = Substitute.For<IApiKeyRepository>();
        // Return null for any key lookup
        repo.GetByKeyHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ApiKey?>(null));

        using var host = BuildHost(repo);
        await host.StartAsync();
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/chat/completions");
        request.Headers.Add("X-Api-Key", "invalid-key-12345");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ValidApiKey_ShouldPassThrough()
    {
        var (plainKey, apiKey) = CreateTestKey();
        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetByKeyHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ApiKey?>(apiKey));

        using var host = BuildHost(repo);
        await host.StartAsync();
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/chat/completions");
        request.Headers.Add("X-Api-Key", plainKey);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("OK");
    }

    [Fact]
    public async Task ValidApiKey_ShouldSetContextItems()
    {
        var (plainKey, apiKey) = CreateTestKey("my-test-key");
        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetByKeyHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ApiKey?>(apiKey));

        using var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(repo);
                    })
                    .Configure(app =>
                    {
                        app.UseMiddleware<ApiKeyAuthMiddleware>();
                        app.Run(async ctx =>
                        {
                            // Verify context items were set by middleware
                            ctx.Items["ApiKeyId"].Should().Be(apiKey.Id);
                            ctx.Items["ApiKeyName"].Should().Be("my-test-key");
                            ctx.Items["ApiKeyModelIds"].Should().BeOfType<Guid[]>();
                            ctx.Items["ApiKeyAllowProviderFallback"].Should().Be(false);

                            ctx.Response.StatusCode = 200;
                            await ctx.Response.WriteAsync("OK");
                        });
                    });
            })
            .Build();

        await host.StartAsync();
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/chat/completions");
        request.Headers.Add("X-Api-Key", plainKey);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AuthorizationHeader_WithBearerToken_ShouldWork()
    {
        var (plainKey, apiKey) = CreateTestKey();
        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetByKeyHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ApiKey?>(apiKey));

        using var host = BuildHost(repo);
        await host.StartAsync();
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {plainKey}");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExpiredApiKey_ShouldReturn401()
    {
        var plainKey = ApiKeyHasher.GenerateApiKey();
        var hash = ApiKeyHasher.Hash(plainKey);
        var expiredKey = ApiKey.Create("expired-key", hash, ApiKeyHasher.ExtractPrefix(plainKey), DateTimeOffset.UtcNow.AddDays(-1));

        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetByKeyHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ApiKey?>(expiredKey));

        using var host = BuildHost(repo);
        await host.StartAsync();
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/chat/completions");
        request.Headers.Add("X-Api-Key", plainKey);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InactiveApiKey_ShouldReturn401()
    {
        var (plainKey, apiKey) = CreateTestKey();
        apiKey.Deactivate();

        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetByKeyHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ApiKey?>(apiKey));

        using var host = BuildHost(repo);
        await host.StartAsync();
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/chat/completions");
        request.Headers.Add("X-Api-Key", plainKey);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task V1ModelsEndpoint_ShouldBeAuthenticated()
    {
        var repo = Substitute.For<IApiKeyRepository>();
        using var host = BuildHost(repo);
        await host.StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
