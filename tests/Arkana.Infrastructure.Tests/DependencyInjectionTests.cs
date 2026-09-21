using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Infrastructure.AI;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Repositories;
using Arkana.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Arkana.Infrastructure.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddInfrastructureServices_ShouldRegisterGatewayDbContext()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureServices("Host=localhost;Database=test");

        services.Should().Contain(sd =>
            sd.ServiceType == typeof(GatewayDbContext));
    }

    [Fact]
    public void AddInfrastructureServices_ShouldRegisterApiKeyRepository()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureServices("Host=localhost;Database=test");

        services.Should().Contain(sd =>
            sd.ServiceType == typeof(IApiKeyRepository) &&
            sd.ImplementationType == typeof(ApiKeyRepository));
    }

    [Fact]
    public void AddInfrastructureServices_ShouldRegisterAiProviderRepository()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureServices("Host=localhost;Database=test");

        services.Should().Contain(sd =>
            sd.ServiceType == typeof(IAiProviderRepository) &&
            sd.ImplementationType == typeof(AiProviderRepository));
    }

    [Fact]
    public void AddInfrastructureServices_ShouldRegisterModelRepository()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureServices("Host=localhost;Database=test");

        services.Should().Contain(sd =>
            sd.ServiceType == typeof(IModelRepository) &&
            sd.ImplementationType == typeof(ModelRepository));
    }

    [Fact]
    public void AddInfrastructureServices_ShouldRegisterTokenTracker()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureServices("Host=localhost;Database=test");

        // PERF-ARKANA-005: the queue-backed tracker is now registered.
        // The legacy EfCore* classes still exist (used by the
        // dedicated tracker/logger test files) but they're not
        // the production wiring anymore.
        services.Should().Contain(sd =>
            sd.ServiceType == typeof(ITokenTracker) &&
            sd.ImplementationType == typeof(BatchedTokenTracker));
    }

    [Fact]
    public void AddInfrastructureServices_ShouldRegisterRequestLogger()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureServices("Host=localhost;Database=test");

        services.Should().Contain(sd =>
            sd.ServiceType == typeof(IRequestLogger) &&
            sd.ImplementationType == typeof(BatchedRequestLogger));
    }

    [Fact]
    public void AddInfrastructureServices_ShouldRegisterOpenCodeChatService()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureServices("Host=localhost;Database=test");

        // IChatCompletionService should be registered with named clients
        services.Should().Contain(sd =>
            sd.ServiceType == typeof(IChatCompletionService));
    }

    [Fact]
    public void AddInfrastructureServices_ShouldRegisterModelRouter()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureServices("Host=localhost;Database=test");

        // Deliberately scoped: ModelRouter eager-resolves IEnumerable<IChatCompletionService>,
        // whose connectors depend on scoped services (request DbContext, OAuth). A singleton
        // router captures those instances for the process lifetime, sharing one DbContext and
        // its change tracker across every request — stale tracked ProviderAccount snapshots
        // then break the provider-account version fencing ("Provider account changed").
        services.Should().ContainSingle(sd =>
            sd.ServiceType == typeof(IModelRouter) &&
            sd.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInfrastructureServices_ShouldKeepProviderConnectorFactoryScoped()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureServices("Host=localhost;Database=test");

        services.Should().ContainSingle(sd =>
            sd.ServiceType == typeof(IProviderConnectorFactory) &&
            sd.ImplementationType == typeof(ProviderConnectorFactory) &&
            sd.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInfrastructureServices_ShouldRegisterGeminiAccountServiceAsScoped()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureServices("Host=localhost;Database=test");

        services.Should().ContainSingle(sd =>
            sd.ServiceType == typeof(GeminiAccountService) &&
            sd.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInfrastructureServices_WithNullOptionalParams_ShouldNotThrow()
    {
        var services = new ServiceCollection();

        var act = () => services.AddInfrastructureServices(
            "Host=localhost;Database=test",
            openCodeBaseUrl: null,
            redisConnectionString: null);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddInfrastructureServices_ShouldRegisterRepositoriesAsScoped()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureServices("Host=localhost;Database=test");

        services.Should().Contain(sd =>
            sd.ServiceType == typeof(IApiKeyRepository) &&
            sd.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(sd =>
            sd.ServiceType == typeof(IAiProviderRepository) &&
            sd.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(sd =>
            sd.ServiceType == typeof(IModelRepository) &&
            sd.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInfrastructureServices_ShouldRegisterOpenCodeStreamingClient()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureServices("Host=localhost;Database=test");

        // The "opencode-streaming" named HttpClient should be registered
        services.Should().Contain(sd =>
            sd.ServiceType == typeof(IHttpClientFactory));
    }

    [Fact]
    public void AddInfrastructureServices_ShouldRegisterBudgetResetServiceAsHostedService()
    {
        // C3 regression: the monthly budget rollover must actually start.
        // If AddHostedService<BudgetResetService>() is missing, budgets never
        // reset and a tenant at its cap is stuck at 402 forever.
        var services = new ServiceCollection();

        services.AddInfrastructureServices("Host=localhost;Database=test");

        services.Should().Contain(sd =>
            sd.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
            sd.ImplementationType == typeof(BudgetResetService));
    }

    [Fact]
    public void AddInfrastructureServices_NotThrow_WhenCalledMultipleTimes()
    {
        var services = new ServiceCollection();

        var act = () =>
        {
            services.AddInfrastructureServices("Host=localhost;Database=test");
            services.AddInfrastructureServices("Host=localhost;Database=test");
        };

        act.Should().NotThrow();
    }
}
