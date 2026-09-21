namespace Arkana.Gateway.Api.Tests.Endpoints;

using System.Net;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Arkana.Gateway.Api.Endpoints;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Broker;
using Arkana.Infrastructure.Security;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

public sealed partial class AdminEndpointsTests
{
    private sealed class TestTenantProvider : ITenantProvider
    {
        public Guid? TenantId => Arkana.Gateway.Api.Services.ProviderAccountDashboardFacade.DefaultTenantId;
    }
    /// <summary>
    /// Creates a minimal WebApplication with MapAdminEndpoints and default mocks
    /// for all dependencies that the admin endpoints require.
    /// Specific mocks passed as parameters override the defaults.
    /// </summary>
    private static async Task<WebApplication> CreateAdminHost(
        IApiKeyRepository? apiKeyRepo = null,
        IAiProviderRepository? providerRepo = null,
        ITokenTracker? tracker = null,
        IMediator? mediator = null,
        GatewayDbContext? dbContext = null,
        IProviderCatalog? providerCatalog = null,
        IProviderAccountRepository? providerAccountRepo = null,
        ICLIProxyManagementClientFactory? brokerFactory = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // Register defaults for all admin endpoint dependencies
        builder.Services.AddSingleton(apiKeyRepo ?? Substitute.For<IApiKeyRepository>());
        builder.Services.AddSingleton(providerRepo ?? Substitute.For<IAiProviderRepository>());
        builder.Services.AddSingleton(providerCatalog ?? Substitute.For<IProviderCatalog>());
        builder.Services.AddSingleton(providerAccountRepo ?? Substitute.For<IProviderAccountRepository>());
        builder.Services.AddSingleton(brokerFactory ?? Substitute.For<ICLIProxyManagementClientFactory>());
        builder.Services.AddSingleton<ITenantProvider>(new TestTenantProvider());
        builder.Services.AddSingleton(tracker ?? Substitute.For<ITokenTracker>());
        builder.Services.AddSingleton(mediator ?? Substitute.For<IMediator>());

        // SECURITY: admin endpoints now require ICredentialVault for credential
        // updates. Register a test vault so the endpoint lambdas can resolve it.
        builder.Services.AddSingleton<ICredentialVault>(TestVaultFactory.Create());

        // ChatGPT account management endpoints (this test host owns an
        // InMemory GatewayDbContext, so ChatGptAccountService can resolve).
        builder.Services.AddScoped<Arkana.Infrastructure.Services.ChatGptAccountService>();
        builder.Services.AddScoped<Arkana.Infrastructure.Persistence.Repositories.IProviderAccountOperationRepository>(_ => Substitute.For<Arkana.Infrastructure.Persistence.Repositories.IProviderAccountOperationRepository>());
        builder.Services.AddScoped<Arkana.Gateway.Api.Services.ProviderAccountDashboardFacade>();

        // Register GatewayDbContext with InMemory database if not provided
        if (dbContext != null)
        {
            builder.Services.AddSingleton(dbContext);
        }
        else
        {
            var options = new DbContextOptionsBuilder<GatewayDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            builder.Services.AddSingleton(new GatewayDbContext(options));
        }

        builder.Services.AddHttpClient();
        builder.Services.AddLogging();

        var app = builder.Build();
        app.MapAdminEndpoints();
        app.MapProviderAccountEndpoints();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task GetApiKeys_ShouldReturnOk()
    {
        var keys = new List<ApiKey>
        {
            ApiKey.Create("key1", "abcdef1234567890hash1", "arkana-key1"),
            ApiKey.Create("key2", "abcdef1234567890hash2", "arkana-key2"),
        };
        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetAllAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(keys);

        using var host = await CreateAdminHost(apiKeyRepo: repo);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/admin/api-keys");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetApiKeys_ShouldReturnEmptyList()
    {
        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetAllAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<ApiKey>());

        using var host = await CreateAdminHost(apiKeyRepo: repo);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/admin/api-keys");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Be("[]");
    }

    [Fact]
    public async Task GetProviders_ShouldReturnOk()
    {
        var provider = AiProvider.Create("Test Provider", "test", 0);
        var providers = new List<AiProvider> { provider };
        var repo = Substitute.For<IAiProviderRepository>();
        repo.GetAllAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(providers);

        using var host = await CreateAdminHost(providerRepo: repo);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/admin/providers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetProviders_ShouldReturnEmptyList()
    {
        var repo = Substitute.For<IAiProviderRepository>();
        repo.GetAllAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AiProvider>());

        using var host = await CreateAdminHost(providerRepo: repo);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/admin/providers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Be("[]");
    }

    [Fact]
    public async Task GetLogs_ShouldReturnOk()
    {
        var usages = new List<TokenUsage>
        {
            new("OpenCode", "deepseek-v4-flash", 10, 20, 0.0001m, TimeSpan.FromSeconds(1)),
        };
        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetRecentUsageAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(usages);

        using var host = await CreateAdminHost(tracker: tracker);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/admin/logs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetStats_ShouldReturnOk()
    {
        var key = ApiKey.Create("test-key", "hash123", "arkana-testkey");
        var provider = AiProvider.Create("Test Provider", "test", 0);

        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.GetAllAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<ApiKey> { key });

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AiProvider> { provider });

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new List<TokenUsage>());

        using var host = await CreateAdminHost(apiKeyRepo: keyRepo, providerRepo: providerRepo, tracker: tracker);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/admin/stats");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ToggleApiKey_NotFound_ShouldReturn404()
    {
        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetAllAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<ApiKey>());

        using var host = await CreateAdminHost(apiKeyRepo: repo);
        var client = host.GetTestClient();

        var response = await client.PutAsync($"/admin/api-keys/{Guid.NewGuid()}/toggle", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteApiKey_NotFound_ShouldReturn404()
    {
        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetAllAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<ApiKey>());

        using var host = await CreateAdminHost(apiKeyRepo: repo);
        var client = host.GetTestClient();

        var response = await client.DeleteAsync($"/admin/api-keys/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
