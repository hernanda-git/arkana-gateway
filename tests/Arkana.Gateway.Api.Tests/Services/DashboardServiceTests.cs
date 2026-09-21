using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Arkana.Gateway.Api.Services;
using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;

// ChatMessage is defined in Arkana.Domain.Interfaces

namespace Arkana.Gateway.Api.Tests.Services;

public sealed class DashboardServiceTests
{
    private static DashboardService CreateService(
        IAiProviderRepository? providers = null,
        IApiKeyRepository? apiKeys = null,
        IModelRepository? models = null,
        ITokenTracker? tracker = null,
        IRequestLogger? logger = null,
        ActiveStreamCounter? counter = null,
        IN8nService? n8n = null,
        UserTimeService? userTime = null,
        ICredentialVault? vault = null,
        IHttpClientFactory? httpClientFactory = null,
        IOAuthFlowService? oauth = null,
        IOAuthProviderConfigRepository? oauthConfigs = null,
        IProviderAccountRepository? providerAccounts = null,
        ITenantProvider? tenantProvider = null)
    {
        var config = userTime is null
            ? Substitute.For<IConfiguration>()
            : null;
        return new DashboardService(
            providers ?? Substitute.For<IAiProviderRepository>(),
            apiKeys ?? Substitute.For<IApiKeyRepository>(),
            models ?? Substitute.For<IModelRepository>(),
            tracker ?? Substitute.For<ITokenTracker>(),
            logger ?? Substitute.For<IRequestLogger>(),
            counter ?? new ActiveStreamCounter(),
            n8n ?? Substitute.For<IN8nService>(),
            userTime ?? new UserTimeService(config!),
            vault ?? Substitute.For<ICredentialVault>(),
            new UrlSafetyValidator(new UrlSafetyOptions { AllowHttp = true, AllowPrivateAddresses = true }, new DnsResolver()),
            httpClientFactory ?? Substitute.For<IHttpClientFactory>(),
            Substitute.For<IApiKeyPoolRepository>(),
            Substitute.For<IAgentRepository>(),
            new AgentOrchestrator(
                Substitute.For<IAgentRepository>(),
                Substitute.For<IModelRepository>(),
                Substitute.For<IAiProviderRepository>(),
                Substitute.For<IProviderConnectorFactory>(),
                Substitute.For<ILogger<AgentOrchestrator>>()),
            oauth ?? Substitute.For<IOAuthFlowService>(),
            oauthConfigs ?? Substitute.For<IOAuthProviderConfigRepository>(),
            Substitute.For<Microsoft.AspNetCore.Components.NavigationManager>(),
            providerAccounts: providerAccounts,
            tenantProvider: tenantProvider);
    }

    private static ITenantProvider DefaultTenantProvider()
    {
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(ProviderAccountDashboardFacade.DefaultTenantId);
        return tenant;
    }

    [Fact]
    public async Task GetProvidersAsync_WithoutTenant_FailsClosed()
    {
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync().Returns(CreateProviders((Guid.NewGuid(), "Foreign", "foreign", true)));
        var service = CreateService(providers: providerRepo);

        var result = await service.GetProvidersAsync();

        result.Should().BeEmpty();
        await providerRepo.DidNotReceive().GetAllAsync();
    }

    [Fact]
    public async Task ToggleProviderAsync_WithoutTenant_ThrowsBeforeRepositoryAccess()
    {
        var providerRepo = Substitute.For<IAiProviderRepository>();
        var service = CreateService(providers: providerRepo);

        var act = () => service.ToggleProviderAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authenticated tenant is required*");
        await providerRepo.DidNotReceiveWithAnyArgs()
            .GetByIdAsync(default(Guid), default(Guid), default(CancellationToken));
    }

    [Fact]
    public async Task ToggleProviderAsync_CrossTenantId_DoesNotMutateForeignProvider()
    {
        var providerId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetByIdAsync(providerId, tenantId).Returns((AiProvider?)null);
        var service = CreateService(providers: providerRepo, tenantProvider: TenantProvider(tenantId));

        await service.ToggleProviderAsync(providerId);

        await providerRepo.DidNotReceive()
            .UpdateAsync(Arg.Any<AiProvider>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateOAuthAccountAsync_WithoutTenant_ThrowsBeforePersistence()
    {
        var providerRepo = Substitute.For<IAiProviderRepository>();
        var service = CreateService(providers: providerRepo);

        var act = () => service.CreateOAuthAccountAsync(Guid.NewGuid(), "gemini", "Account");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authenticated tenant is required*");
        await providerRepo.DidNotReceive()
            .AddAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>());
    }

    private static ITenantProvider TenantProvider(Guid tenantId)
    {
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);
        return tenant;
    }

    private static List<AiProvider> CreateProviders(params (Guid Id, string Name, string Code, bool IsEnabled)[] items)
    {
        return items.Select(i =>
        {
            var p = AiProvider.Create(i.Name, i.Code, 0);
            // Use reflection or modify via setter since private setters
            var field = typeof(AiProvider).GetField("<Id>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field?.SetValue(p, i.Id);
            if (!i.IsEnabled)
            {
                p.Disable();
            }
            return p;
        }).ToList();
    }

    private static List<ApiKey> CreateApiKeys(params (Guid Id, string Name, string Hash, bool IsActive)[] items)
    {
        return items.Select(i =>
        {
            var key = ApiKey.Create(i.Name, i.Hash, "arkana-apply");
            var idField = typeof(ApiKey).GetField("<Id>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            idField?.SetValue(key, i.Id);
            var hashField = typeof(ApiKey).GetField("<KeyHash>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            hashField?.SetValue(key, i.Hash);
            if (!i.IsActive)
            {
                key.Deactivate();
            }
            return key;
        }).ToList();
    }

    private static List<Model> CreateModels(AiProvider provider, params (Guid Id, string Code, string Name)[] items)
    {
        return items.Select(i =>
        {
            var m = Model.Create(provider.Id, i.Name, i.Code);
            var idField = typeof(Model).GetField("<Id>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            idField?.SetValue(m, i.Id);
            var providerField = typeof(Model).GetField("<Provider>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            providerField?.SetValue(m, provider);
            return m;
        }).ToList();
    }

    private static TokenUsage CreateTokenUsage(string provider, string model, int input, int output, decimal cost,
        string? apiKeyName = null, DateTimeOffset? timestamp = null)
    {
        return new TokenUsage(provider, model, input, output, cost, TimeSpan.FromMilliseconds(100), apiKeyName)
        {
            Timestamp = timestamp ?? DateTimeOffset.UtcNow
        };
    }

    // ──────────────────────────────────────────────────────────
    //  Constructor
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_ShouldNotThrow()
    {
        var act = () => CreateService();
        act.Should().NotThrow();
    }

    // ──────────────────────────────────────────────────────────
    //  GetDashboardStatsAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDashboardStatsAsync_ShouldReturnCorrectCounts()
    {
        var providers = CreateProviders(
            (Guid.NewGuid(), "ProviderA", "pa", true),
            (Guid.NewGuid(), "ProviderB", "pb", false),
            (Guid.NewGuid(), "ProviderC", "pc", true));
        var keys = CreateApiKeys(
            (Guid.NewGuid(), "Key1", "hash1", true),
            (Guid.NewGuid(), "Key2", "hash2", false),
            (Guid.NewGuid(), "Key3", "hash3", true),
            (Guid.NewGuid(), "Key4", "hash4", false));
        var counter = new ActiveStreamCounter();
        counter.Increment();
        counter.Increment();
        counter.Increment();

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync().Returns(providers);
        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.GetAllAsync().Returns(keys);

        var service = CreateService(providers: providerRepo, apiKeys: keyRepo, counter: counter);

        var result = await service.GetDashboardStatsAsync();

        result.Should().NotBeNull();
        result.TotalApiKeys.Should().Be(4);
        result.ActiveApiKeys.Should().Be(2);
        result.TotalProviders.Should().Be(3);
        result.ActiveProviders.Should().Be(2);
        result.ActiveStreams.Should().Be(3);
    }

    [Fact]
    public async Task GetDashboardStatsAsync_WithEmptyData_ShouldReturnZeros()
    {
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync().Returns(new List<AiProvider>());
        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.GetAllAsync().Returns(new List<ApiKey>());

        var service = CreateService(providers: providerRepo, apiKeys: keyRepo);

        var result = await service.GetDashboardStatsAsync();

        result.TotalApiKeys.Should().Be(0);
        result.ActiveApiKeys.Should().Be(0);
        result.TotalProviders.Should().Be(0);
        result.ActiveProviders.Should().Be(0);
        result.ActiveStreams.Should().Be(0);
    }

    [Fact]
    public async Task GetDashboardStatsAsync_WithoutWorkflows_ShouldNotWaitForN8n()
    {
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync().Returns(new List<AiProvider>());
        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.GetAllAsync().Returns(new List<ApiKey>());
        var n8n = Substitute.For<IN8nService>();

        var service = CreateService(providers: providerRepo, apiKeys: keyRepo, n8n: n8n);

        var result = await service.GetDashboardStatsAsync(includeWorkflows: false);

        result.TotalWorkflows.Should().Be(0);
        result.ActiveWorkflows.Should().Be(0);
        await n8n.DidNotReceive().GetWorkflowsAsync();
    }

    // ──────────────────────────────────────────────────────────
    //  GetUsageStatsAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUsageStatsAsync_DailyRange_ShouldFilterByToday()
    {
        var now = DateTimeOffset.UtcNow;
        var todayUsage = CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m,
            timestamp: now);
        var yesterdayUsage = CreateTokenUsage("ProviderA", "model-a", 10, 5, 0.0001m,
            timestamp: now.AddDays(-1));

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>())
            .Returns(new List<TokenUsage> { todayUsage, yesterdayUsage });

        var service = CreateService(tracker: tracker);

        var result = await service.GetUsageStatsAsync(DashboardService.TimeRange.Daily);

        result.Should().NotBeNull();
        result.Requests.Should().Be(2); // Both returned, filtering is date-based at tracker level
    }

    [Fact]
    public async Task GetUsageStatsAsync_WithProviderFilter_ShouldFilterUsages()
    {
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m, apiKeyName: "key1"),
            CreateTokenUsage("ProviderB", "model-b", 200, 100, 0.002m, apiKeyName: "key2"),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);

        var service = CreateService(tracker: tracker);

        var result = await service.GetUsageStatsAsync(DashboardService.TimeRange.All, providerFilter: "ProviderA");

        result.Requests.Should().Be(1);
        result.InputTokens.Should().Be(100);
        result.OutputTokens.Should().Be(50);
        result.Cost.Should().Be(0.001m);
    }

    [Fact]
    public async Task GetUsageStatsAsync_WithApiKeyFilter_ShouldFilterUsages()
    {
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m, apiKeyName: "key1"),
            CreateTokenUsage("ProviderB", "model-b", 200, 100, 0.002m, apiKeyName: "key2"),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);

        var service = CreateService(tracker: tracker);

        var result = await service.GetUsageStatsAsync(DashboardService.TimeRange.All, apiKeyFilter: "key1");

        result.Requests.Should().Be(1);
        result.InputTokens.Should().Be(100);
    }

    [Fact]
    public async Task GetUsageStatsAsync_WithWildcardFilter_ShouldNotFilter()
    {
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m),
            CreateTokenUsage("ProviderB", "model-b", 200, 100, 0.002m),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);

        var service = CreateService(tracker: tracker);

        var result = await service.GetUsageStatsAsync(DashboardService.TimeRange.All,
            providerFilter: "*", apiKeyFilter: "*");

        result.Requests.Should().Be(2);
    }

    [Fact]
    public async Task GetUsageStatsAsync_WeeklyRange_ShouldCallTracker()
    {
        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>())
            .Returns(new List<TokenUsage>());

        var service = CreateService(tracker: tracker);

        var result = await service.GetUsageStatsAsync(DashboardService.TimeRange.Weekly);

        result.Requests.Should().Be(0);
        await tracker.Received(1).GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>());
    }

    [Fact]
    public async Task GetUsageStatsAsync_MonthlyRange_ShouldCallTracker()
    {
        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>())
            .Returns(new List<TokenUsage>());

        var service = CreateService(tracker: tracker);

        var result = await service.GetUsageStatsAsync(DashboardService.TimeRange.Monthly);

        result.Requests.Should().Be(0);
        await tracker.Received(1).GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>());
    }

    [Fact]
    public async Task GetUsageStatsAsync_YearlyRange_ShouldCallTracker()
    {
        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>())
            .Returns(new List<TokenUsage>());

        var service = CreateService(tracker: tracker);

        var result = await service.GetUsageStatsAsync(DashboardService.TimeRange.Yearly);

        result.Requests.Should().Be(0);
        await tracker.Received(1).GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>());
    }

    // ──────────────────────────────────────────────────────────
    //  GetProviderNamesAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProviderNamesAsync_ShouldReturnSortedNames()
    {
        var providers = CreateProviders(
            (Guid.NewGuid(), "Zeta", "zeta", true),
            (Guid.NewGuid(), "Alpha", "alpha", true),
            (Guid.NewGuid(), "Beta", "beta", true));

        var repo = Substitute.For<IAiProviderRepository>();
        repo.GetAllAsync().Returns(providers);

        var service = CreateService(providers: repo);

        var result = await service.GetProviderNamesAsync();

        result.Should().BeInAscendingOrder();
        result.Should().ContainInOrder("Alpha", "Beta", "Zeta");
    }

    [Fact]
    public async Task GetProviderNamesAsync_WhenEmpty_ShouldReturnEmptyList()
    {
        var repo = Substitute.For<IAiProviderRepository>();
        repo.GetAllAsync().Returns(new List<AiProvider>());

        var service = CreateService(providers: repo);

        var result = await service.GetProviderNamesAsync();

        result.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────────
    //  GetApiKeyNamesAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetApiKeyNamesAsync_ShouldReturnSortedNames()
    {
        var keys = CreateApiKeys(
            (Guid.NewGuid(), "Z-key", "hash1", true),
            (Guid.NewGuid(), "A-key", "hash2", true),
            (Guid.NewGuid(), "M-key", "hash3", true));

        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetAllAsync().Returns(keys);

        var service = CreateService(apiKeys: repo);

        var result = await service.GetApiKeyNamesAsync();

        result.Should().BeInAscendingOrder();
        result.Should().ContainInOrder("A-key", "M-key", "Z-key");
    }

    [Fact]
    public async Task GetApiKeyNamesAsync_WhenEmpty_ShouldReturnEmptyList()
    {
        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetAllAsync().Returns(new List<ApiKey>());

        var service = CreateService(apiKeys: repo);

        var result = await service.GetApiKeyNamesAsync();

        result.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────────
    //  GetModelUsageAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetModelUsageAsync_ShouldReturnUsagePerModel()
    {
        var providerId = Guid.NewGuid();
        var provider = CreateProviders((providerId, "ProviderA", "pa", true)).First();
        var modelId = Guid.NewGuid();
        var models = CreateModels(provider, (modelId, "model-a", "Model A"));

        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m),
            CreateTokenUsage("ProviderA", "model-a", 200, 100, 0.002m),
        };

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync().Returns(new List<AiProvider> { provider });
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(models);
        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);

        var service = CreateService(providers: providerRepo, models: modelRepo, tracker: tracker);

        var result = await service.GetModelUsageAsync(DashboardService.TimeRange.All);

        result.Should().HaveCount(1);
        var view = result[0];
        view.ProviderId.Should().Be(providerId);
        view.ProviderName.Should().Be("ProviderA");
        view.ModelId.Should().Be(modelId);
        view.ModelCode.Should().Be("model-a");
        view.Requests.Should().Be(2);
        view.InputTokens.Should().Be(300);
        view.OutputTokens.Should().Be(150);
        view.Cost.Should().Be(0.003m);
    }

    [Fact]
    public async Task GetModelUsageAsync_WhenProviderHasNoModels_ShouldReturnProviderLevelRow()
    {
        var providerId = Guid.NewGuid();
        var provider = CreateProviders((providerId, "ProviderA", "pa", true)).First();

        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m),
        };

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync().Returns(new List<AiProvider> { provider });
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());
        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);

        var service = CreateService(providers: providerRepo, models: modelRepo, tracker: tracker);

        var result = await service.GetModelUsageAsync(DashboardService.TimeRange.All);

        result.Should().HaveCount(1);
        var view = result[0];
        view.ModelId.Should().Be(Guid.Empty);
        view.ModelName.Should().Be("");
        view.ModelCode.Should().Be("");
        view.Requests.Should().Be(1);
    }

    [Fact]
    public async Task GetModelUsageAsync_WithFilters_ShouldFilterUsages()
    {
        var provider = CreateProviders((Guid.NewGuid(), "ProviderA", "pa", true)).First();
        var models = CreateModels(provider, (Guid.NewGuid(), "model-a", "Model A"));

        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m, apiKeyName: "key1"),
            CreateTokenUsage("ProviderB", "model-b", 200, 100, 0.002m, apiKeyName: "key2"),
        };

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync().Returns(new List<AiProvider> { provider });
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(models);
        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);

        var service = CreateService(providers: providerRepo, models: modelRepo, tracker: tracker);

        var result = await service.GetModelUsageAsync(DashboardService.TimeRange.All, providerFilter: "ProviderA");

        result.Should().HaveCount(1);
        result[0].Requests.Should().Be(1);
        result[0].InputTokens.Should().Be(100);
    }

    [Fact]
    public async Task GetModelUsageAsync_ShouldNotDuplicateSameModelCodeAcrossProviders()
    {
        var providerA = CreateProviders((Guid.NewGuid(), "ProviderA", "pa", true)).First();
        var providerB = CreateProviders((Guid.NewGuid(), "ProviderB", "pb", true)).First();
        var models = CreateModels(providerA, (Guid.NewGuid(), "gpt-5.6-luna", "Gpt 5.6 Luna"))
            .Concat(CreateModels(providerB, (Guid.NewGuid(), "gpt-5.6-luna", "Gpt 5.6 Luna")))
            .ToList();
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "gpt-5.6-luna", 100, 50, 0.001m),
        };

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync().Returns(new List<AiProvider> { providerA, providerB });
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(models);
        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);

        var service = CreateService(providers: providerRepo, models: modelRepo, tracker: tracker);

        var result = await service.GetModelUsageAsync(DashboardService.TimeRange.All);

        result.Where(x => x.Requests > 0).Should().ContainSingle();
        result.Single(x => x.Requests > 0).ProviderName.Should().Be("ProviderA");
    }

    [Fact]
    public async Task GetOwnedModelUsageAsync_ShouldExcludeUsageFromOtherApiKeys()
    {
        var ownerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();
        var tenantId = ProviderAccountDashboardFacade.DefaultTenantId;
        var provider = CreateProviders((Guid.NewGuid(), "ProviderA", "pa", true)).First();
        var models = CreateModels(provider, (Guid.NewGuid(), "model-a", "Model A"));
        var ownedKey = ApiKey.Create("User key", "owned-hash", "arkana-owned", tenantId);
        var otherKey = ApiKey.Create("Other key", "other-hash", "arkana-other", tenantId);
        typeof(ApiKey).GetField("<OwnerUserId>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(ownedKey, ownerId);
        typeof(ApiKey).GetField("<OwnerUserId>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(otherKey, otherOwnerId);

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync().Returns(new List<AiProvider> { provider });
        providerRepo.GetAllAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns(new List<AiProvider> { provider });
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(models);
        modelRepo.GetAllAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns(models);
        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.GetAllAsync().Returns(new List<ApiKey> { ownedKey, otherKey });
        keyRepo.GetAllAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey> { ownedKey, otherKey });
        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(
        [
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m, "User key"),
            CreateTokenUsage("ProviderA", "model-a", 900, 450, 0.009m, "Other key")
        ]);

        var service = CreateService(providers: providerRepo, apiKeys: keyRepo,
            models: modelRepo, tracker: tracker, tenantProvider: TenantProvider(tenantId));

        var result = await service.GetOwnedModelUsageAsync(ownerId, DashboardService.TimeRange.All);

        result.Should().ContainSingle(x => x.Requests == 1 && x.InputTokens == 100 && x.Cost == 0.001m);
    }

    // ──────────────────────────────────────────────────────────
    //  GetProvidersAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProvidersAsync_ShouldReturnProviderViews()
    {
        var providerId = Guid.NewGuid();
        var providers = CreateProviders((providerId, "ProviderA", "pa", true));

        var repo = Substitute.For<IAiProviderRepository>();
        repo.GetAllAsync(ProviderAccountDashboardFacade.DefaultTenantId).Returns(providers);
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(ProviderAccountDashboardFacade.DefaultTenantId);

        var service = CreateService(providers: repo, tenantProvider: tenantProvider);

        var result = await service.GetProvidersAsync();

        result.Should().HaveCount(1);
        var view = result[0];
        view.Id.Should().Be(providerId);
        view.Name.Should().Be("ProviderA");
        view.Code.Should().Be("pa");
        view.IsEnabled.Should().BeTrue();
        view.CostPerInputToken.Should().Be(0);
    }

    [Fact]
    public async Task GetProvidersAsync_WhenEmpty_ShouldReturnEmptyList()
    {
        var repo = Substitute.For<IAiProviderRepository>();
        repo.GetAllAsync(ProviderAccountDashboardFacade.DefaultTenantId).Returns(new List<AiProvider>());
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(ProviderAccountDashboardFacade.DefaultTenantId);

        var service = CreateService(providers: repo, tenantProvider: tenantProvider);

        var result = await service.GetProvidersAsync();

        result.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────────
    //  ToggleProviderAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ToggleProviderAsync_ShouldDisableEnabledProvider()
    {
        var providerId = Guid.NewGuid();
        var provider = CreateProviders((providerId, "ProviderA", "pa", true)).First();

        var repo = Substitute.For<IAiProviderRepository>();
        repo.GetByIdAsync(providerId, ProviderAccountDashboardFacade.DefaultTenantId).Returns(provider);
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(ProviderAccountDashboardFacade.DefaultTenantId);

        var service = CreateService(providers: repo, tenantProvider: tenantProvider);

        await service.ToggleProviderAsync(providerId);

        provider.IsEnabled.Should().BeFalse();
        await repo.Received(1).UpdateAsync(provider, ProviderAccountDashboardFacade.DefaultTenantId);
    }

    [Fact]
    public async Task ToggleProviderAsync_ShouldEnableDisabledProvider()
    {
        var providerId = Guid.NewGuid();
        var provider = CreateProviders((providerId, "ProviderA", "pa", false)).First();

        var repo = Substitute.For<IAiProviderRepository>();
        repo.GetByIdAsync(providerId, ProviderAccountDashboardFacade.DefaultTenantId).Returns(provider);
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(ProviderAccountDashboardFacade.DefaultTenantId);

        var service = CreateService(providers: repo, tenantProvider: tenantProvider);

        await service.ToggleProviderAsync(providerId);

        provider.IsEnabled.Should().BeTrue();
        await repo.Received(1).UpdateAsync(provider, ProviderAccountDashboardFacade.DefaultTenantId);
    }

    [Fact]
    public async Task ToggleProviderAsync_WhenProviderNotFound_ShouldDoNothing()
    {
        var repo = Substitute.For<IAiProviderRepository>();
        repo.GetByIdAsync(Arg.Any<Guid>(), ProviderAccountDashboardFacade.DefaultTenantId)
            .Returns((AiProvider?)null);
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(ProviderAccountDashboardFacade.DefaultTenantId);

        var service = CreateService(providers: repo, tenantProvider: tenantProvider);

        await service.ToggleProviderAsync(Guid.NewGuid());

        await repo.DidNotReceive().UpdateAsync(Arg.Any<AiProvider>());
    }

    // ──────────────────────────────────────────────────────────
    //  GetAllModelsAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllModelsAsync_ShouldReturnModelViews()
    {
        var provider = CreateProviders((Guid.NewGuid(), "ProviderA", "pa", true)).First();
        var models = CreateModels(provider, (Guid.NewGuid(), "model-a", "Model A"));

        var repo = Substitute.For<IModelRepository>();
        repo.GetAllAsync(ProviderAccountDashboardFacade.DefaultTenantId).Returns(models);
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(ProviderAccountDashboardFacade.DefaultTenantId);

        var service = CreateService(models: repo, tenantProvider: tenantProvider);

        var result = await service.GetAllModelsAsync();

        result.Should().HaveCount(1);
        result[0].Id.Should().Be(models[0].Id);
        result[0].ProviderName.Should().Be("ProviderA");
        result[0].Code.Should().Be("model-a");
    }

    [Fact]
    public async Task GetAllModelsAsync_WhenEmpty_ShouldReturnEmptyList()
    {
        var repo = Substitute.For<IModelRepository>();
        repo.GetAllAsync(ProviderAccountDashboardFacade.DefaultTenantId).Returns(new List<Model>());
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(ProviderAccountDashboardFacade.DefaultTenantId);

        var service = CreateService(models: repo, tenantProvider: tenantProvider);

        var result = await service.GetAllModelsAsync();

        result.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────────
    //  GetModelsByProviderAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetModelsByProviderAsync_ShouldReturnFilteredModels()
    {
        var providerId = Guid.NewGuid();
        var provider = CreateProviders((providerId, "ProviderA", "pa", true)).First();
        var models = CreateModels(provider, (Guid.NewGuid(), "model-a", "Model A"));

        var repo = Substitute.For<IModelRepository>();
        repo.GetByProviderIdAsync(providerId, ProviderAccountDashboardFacade.DefaultTenantId).Returns(models);
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(ProviderAccountDashboardFacade.DefaultTenantId);

        var service = CreateService(models: repo, tenantProvider: tenantProvider);

        var result = await service.GetModelsByProviderAsync(providerId);

        result.Should().HaveCount(1);
        result[0].Code.Should().Be("model-a");
    }

    [Fact]
    public async Task GetModelsByProviderAsync_WhenEmpty_ShouldReturnEmptyList()
    {
        var repo = Substitute.For<IModelRepository>();
        repo.GetByProviderIdAsync(Arg.Any<Guid>(), ProviderAccountDashboardFacade.DefaultTenantId)
            .Returns(new List<Model>());
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(ProviderAccountDashboardFacade.DefaultTenantId);

        var service = CreateService(models: repo, tenantProvider: tenantProvider);

        var result = await service.GetModelsByProviderAsync(Guid.NewGuid());

        result.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────────
    //  GetProvidersWithModelsAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProvidersWithModelsAsync_ShouldReturnProvidersWithModels()
    {
        var providerId = Guid.NewGuid();
        var provider = CreateProviders((providerId, "ProviderA", "pa", true)).First();
        var models = CreateModels(provider, (Guid.NewGuid(), "model-a", "Model A"));

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync(ProviderAccountDashboardFacade.DefaultTenantId).Returns(new List<AiProvider> { provider });
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync(ProviderAccountDashboardFacade.DefaultTenantId).Returns(models);
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(ProviderAccountDashboardFacade.DefaultTenantId);

        var service = CreateService(providers: providerRepo, models: modelRepo, tenantProvider: tenantProvider);

        var result = await service.GetProvidersWithModelsAsync();

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("ProviderA");
        result[0].Models.Should().HaveCount(1);
        result[0].Models[0].Code.Should().Be("model-a");
    }

    [Fact]
    public async Task GetProvidersWithModelsAsync_WhenEmpty_ShouldReturnEmptyList()
    {
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync(ProviderAccountDashboardFacade.DefaultTenantId).Returns(new List<AiProvider>());
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(ProviderAccountDashboardFacade.DefaultTenantId);

        var service = CreateService(providers: providerRepo, models: modelRepo, tenantProvider: tenantProvider);

        var result = await service.GetProvidersWithModelsAsync();

        result.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────────
    //  GetApiKeysAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetApiKeysAsync_ShouldFormatLongPrefix()
    {
        var key = ApiKey.Create("test-key", "abcdef1234567890abcdef1234567890", "arkana-longpref");
        var idField = typeof(ApiKey).GetField("<Id>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        idField?.SetValue(key, Guid.NewGuid());

        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetAllAsync().Returns(new List<ApiKey> { key });

        var service = CreateService(apiKeys: repo);

        var result = await service.GetApiKeysAsync();

        result.Should().HaveCount(1);
        result[0].Prefix.Should().Be("arkana-longpref...");
    }

    [Fact]
    public async Task GetApiKeysAsync_ShouldFormatShortPrefix()
    {
        var key = ApiKey.Create("test-key", "short", "arkana-short");
        var idField = typeof(ApiKey).GetField("<Id>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        idField?.SetValue(key, Guid.NewGuid());
        var hashField = typeof(ApiKey).GetField("<KeyHash>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        hashField?.SetValue(key, "short");

        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetAllAsync().Returns(new List<ApiKey> { key });

        var service = CreateService(apiKeys: repo);

        var result = await service.GetApiKeysAsync();

        result.Should().HaveCount(1);
        result[0].Prefix.Should().Be("arkana-short...");
    }

    [Fact]
    public async Task GetApiKeysAsync_WhenEmpty_ShouldReturnEmptyList()
    {
        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetAllAsync().Returns(new List<ApiKey>());

        var service = CreateService(apiKeys: repo);

        var result = await service.GetApiKeysAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetApiKeysAsync_ExposesExplicitFallbackPolicy()
    {
        var key = ApiKey.Create("fallback-key", "hash", "arkana-fallback");
        key.PreferredProviderCode = "chatgpt-acc2";
        key.AllowProviderFallback = true;
        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetAllAsync().Returns(new List<ApiKey> { key });

        var result = await CreateService(apiKeys: repo).GetApiKeysAsync();

        result.Single().PreferredProviderCode.Should().Be("chatgpt-acc2");
        result.Single().AllowProviderFallback.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateApiKeyRoutingAsync_RejectsModelFromAnotherProvider()
    {
        var key = ApiKey.Create("strict-key", "hash", "arkana-strict");
        var pinned = CreateProviders((Guid.NewGuid(), "Pinned", "pinned", true)).Single();
        var other = CreateProviders((Guid.NewGuid(), "Other", "other", true)).Single();
        var otherModel = CreateModels(other, (Guid.NewGuid(), "other-model", "Other model")).Single();
        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.GetAllAsync().Returns(new List<ApiKey> { key });
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync().Returns(new List<AiProvider> { pinned, other });
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model> { otherModel });

        var act = () => CreateService(providerRepo, keyRepo, modelRepo)
            .UpdateApiKeyRoutingAsync(key.Id, "pinned", false, [otherModel.Id]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not belong to provider 'pinned'*");
        await keyRepo.DidNotReceive().UpdateAsync(Arg.Any<ApiKey>());
    }

    [Fact]
    public async Task UpdateApiKeyRoutingAsync_PersistsStrictPinAndAlignedModels()
    {
        var key = ApiKey.Create("strict-key", "hash", "arkana-strict");
        var pinned = CreateProviders((Guid.NewGuid(), "Pinned", "pinned", true)).Single();
        var model = CreateModels(pinned, (Guid.NewGuid(), "model", "Model")).Single();
        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.GetAllAsync().Returns(new List<ApiKey> { key });
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync().Returns(new List<AiProvider> { pinned });
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model> { model });

        await CreateService(providerRepo, keyRepo, modelRepo)
            .UpdateApiKeyRoutingAsync(key.Id, "pinned", false, [model.Id]);

        key.PreferredProviderCode.Should().Be("pinned");
        key.AllowProviderFallback.Should().BeFalse();
        key.AllowedModels.Should().ContainSingle().Which.Should().Be(model);
        await keyRepo.Received(1).UpdateAsync(key);
    }

    [Fact]
    public async Task GetApiKeyRoutingHistoryAsync_AggregatesActualProviders()
    {
        var logger = Substitute.For<IRequestLogger>();
        logger.SearchAsync(null, null, "strict-key", null, Arg.Any<DateTimeOffset>(), null, 10000,
                Arg.Any<CancellationToken>())
            .Returns(new List<RequestLog>
            {
                new() { ApiKeyName = "strict-key", Provider = "chatgpt-acc2", Timestamp = DateTimeOffset.UtcNow.AddHours(-2) },
                new() { ApiKeyName = "strict-key", Provider = "chatgpt-acc2", Timestamp = DateTimeOffset.UtcNow.AddHours(-1) },
                new() { ApiKeyName = "strict-key", Provider = "chatgpt-acc3", Timestamp = DateTimeOffset.UtcNow, IsError = true }
            });

        var result = await CreateService(logger: logger)
            .GetApiKeyRoutingHistoryAsync("strict-key", 30);

        result.Should().HaveCount(2);
        result.Single(x => x.ProviderCode == "chatgpt-acc2").Requests.Should().Be(2);
        result.Single(x => x.ProviderCode == "chatgpt-acc3").Errors.Should().Be(1);
    }

    // ──────────────────────────────────────────────────────────
    //  CreateApiKeyAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateApiKeyAsync_WithRoutingPersistsStrictProviderBoundary()
    {
        var provider = CreateProviders((Guid.NewGuid(), "Codex A", "chatgpt-acc1", true)).Single();
        var model = CreateModels(provider, (Guid.NewGuid(), "gpt-codex", "GPT Codex")).Single();
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync().Returns(new List<AiProvider> { provider });
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model> { model });
        var keyRepo = Substitute.For<IApiKeyRepository>();

        await CreateService(providerRepo, keyRepo, modelRepo)
            .CreateApiKeyAsync("codex-key", [model.Id], "chatgpt-acc1", false);

        await keyRepo.Received(1).AddAsync(Arg.Is<ApiKey>(k =>
            k.PreferredProviderCode == "chatgpt-acc1"
            && !k.AllowProviderFallback
            && k.AllowedModels.Count == 1));
    }

    [Fact]
    public async Task CreateApiKeyAsync_ShouldCreateKeyWithoutModels()
    {
        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.AddAsync(Arg.Any<ApiKey>()).Returns(Task.CompletedTask);

        var service = CreateService(apiKeys: keyRepo);

        var (id, plainKey) = await service.CreateApiKeyAsync("new-key", new List<Guid>());

        id.Should().NotBeEmpty();
        plainKey.Should().NotBeNullOrEmpty();
        plainKey.Should().StartWith("arkana-");
        await keyRepo.Received(1).AddAsync(Arg.Is<ApiKey>(k =>
            k.Name == "new-key" && k.IsActive));
    }

    [Fact]
    public async Task CreateApiKeyAsync_WithModels_ShouldAssociateModels()
    {
        var modelId = Guid.NewGuid();
        var provider = CreateProviders((Guid.NewGuid(), "ProviderA", "pa", true)).First();
        var models = CreateModels(provider, (modelId, "model-a", "Model A"));

        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(models);
        var keyRepo = Substitute.For<IApiKeyRepository>();

        var service = CreateService(apiKeys: keyRepo, models: modelRepo);

        var (id, plainKey) = await service.CreateApiKeyAsync("new-key", new List<Guid> { modelId });

        id.Should().NotBeEmpty();
        plainKey.Should().NotBeNullOrEmpty();
        await keyRepo.Received(1).AddAsync(Arg.Is<ApiKey>(k =>
            k.AllowedModels.Count == 1));
    }

    [Fact]
    public async Task CreateApiKeyAsync_WithNonexistentModelId_ShouldSkipModel()
    {
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());
        var keyRepo = Substitute.For<IApiKeyRepository>();

        var service = CreateService(apiKeys: keyRepo, models: modelRepo);

        var (id, plainKey) = await service.CreateApiKeyAsync("new-key", new List<Guid> { Guid.NewGuid() });

        await keyRepo.Received(1).AddAsync(Arg.Is<ApiKey>(k =>
            k.AllowedModels.Count == 0));
    }

    // ──────────────────────────────────────────────────────────
    //  ToggleApiKeyAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ToggleApiKeyAsync_ShouldDeactivateActiveKey()
    {
        var keyId = Guid.NewGuid();
        var key = CreateApiKeys((keyId, "test-key", "hash", true)).First();

        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetAllAsync().Returns(new List<ApiKey> { key });

        var service = CreateService(apiKeys: repo);

        await service.ToggleApiKeyAsync(keyId);

        key.IsActive.Should().BeFalse();
        await repo.Received(1).UpdateAsync(key);
    }

    [Fact]
    public async Task ToggleApiKeyAsync_ShouldActivateInactiveKey()
    {
        var keyId = Guid.NewGuid();
        var key = CreateApiKeys((keyId, "test-key", "hash", false)).First();

        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetAllAsync().Returns(new List<ApiKey> { key });

        var service = CreateService(apiKeys: repo);

        await service.ToggleApiKeyAsync(keyId);

        key.IsActive.Should().BeTrue();
        await repo.Received(1).UpdateAsync(key);
    }

    [Fact]
    public async Task ToggleApiKeyAsync_WhenKeyNotFound_ShouldDoNothing()
    {
        var repo = Substitute.For<IApiKeyRepository>();
        repo.GetAllAsync().Returns(new List<ApiKey>());

        var service = CreateService(apiKeys: repo);

        await service.ToggleApiKeyAsync(Guid.NewGuid());

        await repo.DidNotReceive().UpdateAsync(Arg.Any<ApiKey>());
    }

    // ──────────────────────────────────────────────────────────
    //  UpdateKeyModelsAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateKeyModelsAsync_ShouldAddAllowedModels()
    {
        var keyId = Guid.NewGuid();
        var key = CreateApiKeys((keyId, "test-key", "hash", true)).First();
        var modelId = Guid.NewGuid();
        var provider = CreateProviders((Guid.NewGuid(), "ProviderA", "pa", true)).First();
        var models = CreateModels(provider, (modelId, "model-a", "Model A"));

        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.GetAllAsync().Returns(new List<ApiKey> { key });
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(models);

        var service = CreateService(apiKeys: keyRepo, models: modelRepo);

        await service.UpdateKeyModelsAsync(keyId, new List<Guid> { modelId });

        key.AllowedModels.Should().HaveCount(1);
        await keyRepo.Received(1).UpdateAsync(key);
    }

    [Fact]
    public async Task UpdateKeyModelsAsync_ShouldClearModelsWhenEmptyList()
    {
        var keyId = Guid.NewGuid();
        var key = CreateApiKeys((keyId, "test-key", "hash", true)).First();

        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.GetAllAsync().Returns(new List<ApiKey> { key });

        var service = CreateService(apiKeys: keyRepo);

        await service.UpdateKeyModelsAsync(keyId, new List<Guid>());

        key.AllowedModels.Should().BeEmpty();
        await keyRepo.Received(1).UpdateAsync(key);
    }

    [Fact]
    public async Task UpdateKeyModelsAsync_WhenKeyNotFound_ShouldDoNothing()
    {
        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.GetAllAsync().Returns(new List<ApiKey>());

        var service = CreateService(apiKeys: keyRepo);

        await service.UpdateKeyModelsAsync(Guid.NewGuid(), new List<Guid>());

        await keyRepo.DidNotReceive().UpdateAsync(Arg.Any<ApiKey>());
    }

    // ──────────────────────────────────────────────────────────
    //  DeleteApiKeyAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteApiKeyAsync_ShouldRemoveKeyFromDatabase()
    {
        var keyId = Guid.NewGuid();
        var key = CreateApiKeys((keyId, "test-key", "hash123", true)).First();

        // Use in-memory database for the DbContext
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new GatewayDbContext(options);
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync();

        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.GetAllAsync().Returns(new List<ApiKey> { key });

        var service = CreateService(apiKeys: keyRepo);

        await service.DeleteApiKeyAsync(keyId, db);

        db.ApiKeys.Count().Should().Be(0);
    }

    [Fact]
    public async Task DeleteApiKeyAsync_WhenKeyNotFound_ShouldDoNothing()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new GatewayDbContext(options);

        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.GetAllAsync().Returns(new List<ApiKey>());

        var service = CreateService(apiKeys: keyRepo);

        // Should not throw
        await service.DeleteApiKeyAsync(Guid.NewGuid(), db);
    }

    // ──────────────────────────────────────────────────────────
    //  GetNavigationLabel
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void GetNavigationLabel_Daily_ShouldReturnFormattedDate()
    {
        var service = CreateService();
        var date = new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero);
        service.NavigationDate = date;

        var label = service.GetNavigationLabel(DashboardService.TimeRange.Daily);

        label.Should().Be("Sun, Jun 7");
    }

    [Fact]
    public void GetNavigationLabel_Weekly_ShouldReturnDateRange()
    {
        var service = CreateService();
        var date = new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero);
        service.NavigationDate = date;

        var label = service.GetNavigationLabel(DashboardService.TimeRange.Weekly);

        label.Should().Be("Jun 1 – Jun 7, 2026");
    }

    [Fact]
    public void GetNavigationLabel_Monthly_ShouldReturnMonthYear()
    {
        var service = CreateService();
        var date = new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero);
        service.NavigationDate = date;

        var label = service.GetNavigationLabel(DashboardService.TimeRange.Monthly);

        label.Should().Be("June 2026");
    }

    [Fact]
    public void GetNavigationLabel_Yearly_ShouldReturnYear()
    {
        var service = CreateService();
        var date = new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero);
        service.NavigationDate = date;

        var label = service.GetNavigationLabel(DashboardService.TimeRange.Yearly);

        label.Should().Be("2026");
    }

    [Fact]
    public void GetNavigationLabel_All_ShouldReturnAllTime()
    {
        var service = CreateService();

        var label = service.GetNavigationLabel(DashboardService.TimeRange.All);

        label.Should().Be("All Time");
    }

    [Fact]
    public void GetNavigationLabel_WithoutNavigationDate_ShouldUseCurrentDateTime()
    {
        var service = CreateService();

        var label = service.GetNavigationLabel(DashboardService.TimeRange.Daily);

        label.Should().NotBeNullOrEmpty();
    }

    // ──────────────────────────────────────────────────────────
    //  NavigationDate
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void NavigationDate_ShouldGetAndSet()
    {
        var service = CreateService();
        var date = new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero);

        service.NavigationDate = date;

        service.NavigationDate.Should().Be(date);
    }

    // ──────────────────────────────────────────────────────────
    //  GetCostChartDataAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCostChartDataAsync_ShouldReturnChartData()
    {
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m,
                timestamp: new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero)),
            CreateTokenUsage("ProviderA", "model-b", 200, 100, 0.002m,
                timestamp: new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero)),
        };

        var provider = CreateProviders((Guid.NewGuid(), "ProviderA", "pa", true)).First();
        var models = CreateModels(provider,
            (Guid.NewGuid(), "model-a", "Model A"),
            (Guid.NewGuid(), "model-b", "Model B"));

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(models);

        var service = CreateService(tracker: tracker, models: modelRepo);

        var result = await service.GetCostChartDataAsync(2026, 6);

        result.Should().NotBeNull();
        result.Dates.Should().HaveCount(30); // June has 30 days
        result.ModelCodes.Should().Contain("model-a").And.Contain("model-b");
        result.ModelColors.Should().HaveCount(2);
        result.Points.Should().NotBeEmpty();
        result.MaxCost.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetCostChartDataAsync_WithModelFilter_ShouldFilterByModelCode()
    {
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m,
                timestamp: new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero)),
            CreateTokenUsage("ProviderA", "model-b", 200, 100, 0.002m,
                timestamp: new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero)),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());

        var service = CreateService(tracker: tracker, models: modelRepo);

        var result = await service.GetCostChartDataAsync(2026, 6, filterModelCode: "model-a");

        result.ModelCodes.Should().Contain("model-a");
    }

    [Fact]
    public async Task GetCostChartDataAsync_WhenNoUsages_ShouldReturnEmptyChart()
    {
        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(new List<TokenUsage>());
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());

        var service = CreateService(tracker: tracker, models: modelRepo);

        var result = await service.GetCostChartDataAsync(2026, 6);

        result.Points.Should().BeEmpty();
        result.ModelCodes.Should().BeEmpty();
        result.ModelColors.Should().BeEmpty();
        result.MaxCost.Should().Be(1m); // Default when no points
    }

    [Fact]
    public async Task GetCostChartDataAsync_WithApiKeyFilter_ShouldFilterByApiKeyName()
    {
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m, apiKeyName: "key-a", timestamp: new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero)),
            CreateTokenUsage("ProviderA", "model-a", 200, 100, 0.002m, apiKeyName: "key-b", timestamp: new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero)),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());

        var service = CreateService(tracker: tracker, models: modelRepo);

        // Filter for "key-a" → only first usage passes
        var result = await service.GetCostChartDataAsync(2026, 6, filterKeyPrefix: "key-a");

        result.Points.Should().NotBeEmpty();
        result.Points.Sum(p => p.Cost).Should().Be(0.001m); // only key-a's 0.001m
        result.ModelCodes.Should().Contain("model-a");
        result.ModelCodes.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetCostChartDataAsync_WithFullApiKeyName_ShouldFilterExactly()
    {
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m, apiKeyName: "prod-key-01", timestamp: new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero)),
            CreateTokenUsage("ProviderA", "model-b", 200, 100, 0.002m, apiKeyName: "prod-key-02", timestamp: new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero)),
            CreateTokenUsage("ProviderA", "model-a", 50, 25, 0.0005m, apiKeyName: "prod-key-01", timestamp: new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.Zero)),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());

        var service = CreateService(tracker: tracker, models: modelRepo);

        var result = await service.GetCostChartDataAsync(2026, 6, filterKeyPrefix: "prod-key-01");

        result.Points.Should().NotBeEmpty();
        result.Points.Sum(p => p.Cost).Should().Be(0.0015m); // 0.001 + 0.0005, key-b excluded
        result.Points.Should().OnlyContain(p => p.ModelCode == "model-a" || p.ModelCode == "model-b");
    }

    [Fact]
    public async Task GetCostChartDataAsync_ModelColors_ShouldBeStableAcrossCalls()
    {
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m, timestamp: new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero)),
            CreateTokenUsage("ProviderA", "model-b", 200, 100, 0.002m, timestamp: new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero)),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());

        var service = CreateService(tracker: tracker, models: modelRepo);

        var first = await service.GetCostChartDataAsync(2026, 6);
        var second = await service.GetCostChartDataAsync(2026, 6);

        first.ModelColors.Should().BeEquivalentTo(second.ModelColors); // identical dictionary
        first.ModelColors["model-a"].Should().Be(second.ModelColors["model-a"]);
        first.ModelColors["model-b"].Should().Be(second.ModelColors["model-b"]);
        first.ModelColors["model-a"].Should().NotBe(first.ModelColors["model-b"]);
    }

    [Fact]
    public async Task GetCostChartDataAsync_ModelColors_ShouldBeStableAcrossMonths()
    {
        var usagesJune = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m, timestamp: new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero)),
            CreateTokenUsage("ProviderA", "model-b", 200, 100, 0.002m, timestamp: new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero)),
        };
        var usagesJuly = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m, timestamp: new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero)),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Is<DateTimeOffset>(from => from.Month == 6),
                Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(usagesJune);
        tracker.GetUsageAsync(Arg.Is<DateTimeOffset>(from => from.Month == 7),
                Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(usagesJuly);
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());

        var service = CreateService(tracker: tracker, models: modelRepo);

        var june = await service.GetCostChartDataAsync(2026, 6);
        var july = await service.GetCostChartDataAsync(2026, 7);

        june.ModelColors["model-a"].Should().Be(july.ModelColors["model-a"]);
        june.ModelColors.Should().ContainKey("model-b");
        july.ModelColors.Should().NotContainKey("model-b");
    }

    // ──────────────────────────────────────────────────────────
    //  GetUsageChartDataAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUsageChartDataAsync_Daily_ShouldReturnHourlyBreakdown()
    {
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m,
                timestamp: DateTimeOffset.UtcNow),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());

        var service = CreateService(tracker: tracker, models: modelRepo);

        var result = await service.GetUsageChartDataAsync(DashboardService.TimeRange.Daily);

        result.Should().NotBeNull();
        result.PeriodLabels.Should().HaveCount(24); // 24 hours
        result.ModelCodes.Should().Contain("model-a");
    }

    [Fact]
    public async Task GetUsageChartDataAsync_Weekly_ShouldReturnDailyBreakdown()
    {
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m,
                timestamp: DateTimeOffset.UtcNow),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());

        var service = CreateService(tracker: tracker, models: modelRepo);

        var result = await service.GetUsageChartDataAsync(DashboardService.TimeRange.Weekly);

        result.Should().NotBeNull();
        result.PeriodLabels.Should().HaveCount(7); // 7 days
    }

    [Fact]
    public async Task GetUsageChartDataAsync_Monthly_ShouldReturnDailyBreakdown()
    {
        // Set navigation date to a fixed month so we can predict days
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m,
                timestamp: new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero)),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());

        var service = CreateService(tracker: tracker, models: modelRepo);
        service.NavigationDate = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);

        var result = await service.GetUsageChartDataAsync(DashboardService.TimeRange.Monthly);

        result.Should().NotBeNull();
        result.PeriodLabels.Should().HaveCount(30); // Full month (Jun 1 to Jun 30)
    }

    [Fact]
    public async Task GetUsageChartDataAsync_Yearly_ShouldReturnMonthlyBreakdown()
    {
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m,
                timestamp: new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero)),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());

        var service = CreateService(tracker: tracker, models: modelRepo);
        service.NavigationDate = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);

        var result = await service.GetUsageChartDataAsync(DashboardService.TimeRange.Yearly);

        result.Should().NotBeNull();
        result.PeriodLabels.Should().HaveCount(12); // Full year (Jan to Dec)
    }

    [Fact]
    public async Task GetUsageChartDataAsync_All_ShouldReturnYearlyBreakdown()
    {
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m,
                timestamp: new DateTimeOffset(2025, 6, 15, 10, 0, 0, TimeSpan.Zero)),
            CreateTokenUsage("ProviderA", "model-a", 200, 100, 0.002m,
                timestamp: new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero)),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());

        var service = CreateService(tracker: tracker, models: modelRepo);

        var result = await service.GetUsageChartDataAsync(DashboardService.TimeRange.All);

        result.Should().NotBeNull();
        result.PeriodLabels.Should().Contain("2025").And.Contain("2026");
    }

    [Fact]
    public async Task GetUsageChartDataAsync_WithProviderFilter_ShouldFilterData()
    {
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m,
                timestamp: DateTimeOffset.UtcNow),
            CreateTokenUsage("ProviderB", "model-b", 200, 100, 0.002m,
                timestamp: DateTimeOffset.UtcNow),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(usages);
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());

        var service = CreateService(tracker: tracker, models: modelRepo);

        var result = await service.GetUsageChartDataAsync(DashboardService.TimeRange.Daily,
            providerFilter: "ProviderA");

        result.ModelCodes.Should().Contain("model-a");
        result.ModelCodes.Should().NotContain("model-b");
    }

    [Fact]
    public async Task GetUsageChartDataAsync_WhenNoUsages_ShouldReturnEmptyResponse()
    {
        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>())
            .Returns(new List<TokenUsage>());
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetAllAsync().Returns(new List<Model>());

        var service = CreateService(tracker: tracker, models: modelRepo);

        var result = await service.GetUsageChartDataAsync(DashboardService.TimeRange.Daily);

        result.Points.Should().BeEmpty();
        result.ModelCodes.Should().BeEmpty();
        result.ModelColors.Should().BeEmpty();
        result.MaxCost.Should().Be(1m); // Default
    }

    // ──────────────────────────────────────────────────────────
    //  GetModelFilterListAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetModelFilterListAsync_ShouldReturnSortedFilterItems()
    {
        var provider = CreateProviders((Guid.NewGuid(), "ProviderA", "pa", true)).First();
        var models = CreateModels(provider,
            (Guid.NewGuid(), "model-b", "Model B"),
            (Guid.NewGuid(), "model-a", "Model A"));

        var repo = Substitute.For<IModelRepository>();
        repo.GetAllAsync().Returns(models);

        var service = CreateService(models: repo);

        var result = await service.GetModelFilterListAsync();

        result.Should().HaveCount(2);
        result.Should().BeInAscendingOrder(i => i.ProviderName);
        result[0].Code.Should().Be("model-a");
        result[0].ProviderName.Should().Be("ProviderA");
    }

    [Fact]
    public async Task GetModelFilterListAsync_WhenEmpty_ShouldReturnEmptyList()
    {
        var repo = Substitute.For<IModelRepository>();
        repo.GetAllAsync().Returns(new List<Model>());

        var service = CreateService(models: repo);

        var result = await service.GetModelFilterListAsync();

        result.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────────
    //  GetLogsAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLogsAsync_ShouldReturnLogViews()
    {
        var usages = new List<TokenUsage>
        {
            CreateTokenUsage("ProviderA", "model-a", 100, 50, 0.001m),
        };

        var tracker = Substitute.For<ITokenTracker>();
        tracker.GetRecentUsageAsync(Arg.Any<int>()).Returns(usages);

        var service = CreateService(tracker: tracker);

        var result = await service.GetLogsAsync(50);

        result.Should().HaveCount(1);
        result[0].Provider.Should().Be("ProviderA");
        result[0].Model.Should().Be("model-a");
        result[0].InputTokens.Should().Be(100);
        result[0].OutputTokens.Should().Be(50);
        result[0].TotalTokens.Should().Be(150);
        result[0].Cost.Should().Be(0.001m);
    }

    // ──────────────────────────────────────────────────────────
    //  GetFullLogsAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFullLogsAsync_ShouldReturnLogEntryViews()
    {
        var log = new RequestLog
        {
            Id = Guid.NewGuid(),
            Provider = "ProviderA",
            Model = "model-a",
            ApiKeyName = "key1",
            Messages = new List<ChatMessage> { new() { Role = "user", Content = "Hello" } },
            ResponseContent = "Hi there",
            ToolCalls = new List<ToolCallInfo> { new() { Id = "call1", Type = "function", FunctionName = "test", FunctionArguments = "{}" } },
            InputTokens = 100,
            OutputTokens = 50,
            Cost = 0.001m,
            Duration = TimeSpan.FromMilliseconds(200),
            Timestamp = DateTimeOffset.UtcNow,
            IsError = false,
            ErrorMessage = null,
        };

        var logger = Substitute.For<IRequestLogger>();
        logger.GetRecentAsync(Arg.Any<int>()).Returns(new List<RequestLog> { log });

        var service = CreateService(logger: logger);

        var result = await service.GetFullLogsAsync(10);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be(log.Id);
        result[0].Provider.Should().Be("ProviderA");
        result[0].Messages.Should().HaveCount(1);
        result[0].Messages[0].Role.Should().Be("user");
        result[0].Messages[0].Content.Should().Be("Hello");
        result[0].ResponseContent.Should().Be("Hi there");
        result[0].ToolCalls.Should().HaveCount(1);
        result[0].ToolCalls![0].Id.Should().Be("call1");
        result[0].InputTokens.Should().Be(100);
        result[0].OutputTokens.Should().Be(50);
        result[0].TotalTokens.Should().Be(150);
        result[0].DurationMs.Should().Be(200);
    }

    // ──────────────────────────────────────────────────────────
    //  GetLogByIdAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLogByIdAsync_WhenFound_ShouldReturnLogEntry()
    {
        var logId = Guid.NewGuid();
        var log = new RequestLog
        {
            Id = logId,
            Provider = "ProviderA",
            Model = "model-a",
            Messages = new List<ChatMessage>(),
            InputTokens = 100,
            OutputTokens = 50,
            Cost = 0.001m,
            Duration = TimeSpan.Zero,
            Timestamp = DateTimeOffset.UtcNow,
        };

        var logger = Substitute.For<IRequestLogger>();
        logger.GetByIdAsync(logId).Returns(log);

        var service = CreateService(logger: logger);

        var result = await service.GetLogByIdAsync(logId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(logId);
    }

    [Fact]
    public async Task GetLogByIdAsync_WhenNotFound_ShouldReturnNull()
    {
        var logger = Substitute.For<IRequestLogger>();
        logger.GetByIdAsync(Arg.Any<Guid>()).Returns((RequestLog?)null);

        var service = CreateService(logger: logger);

        var result = await service.GetLogByIdAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    // ──────────────────────────────────────────────────────────
    //  SearchLogsAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchLogsAsync_ShouldReturnMatchingLogs()
    {
        var logs = new List<RequestLog>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Provider = "ProviderA",
                Model = "model-a",
                Messages = new List<ChatMessage>(),
                InputTokens = 100,
                OutputTokens = 50,
                Cost = 0.001m,
                Duration = TimeSpan.Zero,
                Timestamp = DateTimeOffset.UtcNow,
            }
        };

        var logger = Substitute.For<IRequestLogger>();
        logger.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(logs);

        var service = CreateService(logger: logger);

        var result = await service.SearchLogsAsync(providerFilter: "ProviderA", searchText: "test");

        result.Should().HaveCount(1);
        result[0].Provider.Should().Be("ProviderA");
    }

    // ── Model sync: empty BaseUrl fallback (regression for silent no-op) ──

    private static AiProvider CreateProviderWithCode(Guid id, string name, string code, string? baseUrl = null)
    {
        var p = AiProvider.Create(name, code, 1, baseUrl: baseUrl);
        typeof(AiProvider)
            .GetField("<Id>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(p, id);
        // DecryptApiKey reads the (null) sealed key -> returns null, which is fine here.
        return p;
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }
        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var r = _respond(request);
            r.RequestMessage = request;
            return Task.FromResult(r);
        }
    }

    [Fact]
    public async Task SyncModelsAsync_OpenAiWithNullBaseUrl_FallsBackToKnownEndpointAndSyncs()
    {
        // Regression: OpenAI was seeded with a null BaseUrl and its chat connector
        // hardcodes the endpoint. Previously SyncModelsAsync silently returned
        // SyncResult(0,0); now it must fall back to the known endpoint and fetch models.
        var providerId = Guid.NewGuid();
        var provider = CreateProviderWithCode(providerId, "OpenAI", "openai");

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetByIdAsync(providerId, ProviderAccountDashboardFacade.DefaultTenantId).Returns(provider);

        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetByProviderIdAsync(providerId, ProviderAccountDashboardFacade.DefaultTenantId, Arg.Any<CancellationToken>()).Returns(new List<Model>());

        var handler = new FakeHttpMessageHandler(req =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"object\":\"list\",\"data\":[{\"id\":\"gpt-4o\"},{\"id\":\"gpt-4o-mini\"}]}",
                    System.Text.Encoding.UTF8, "application/json")
            });
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient().Returns(new HttpClient(handler));

        var service = CreateService(providers: providerRepo, models: modelRepo, httpClientFactory: httpClientFactory,
            tenantProvider: DefaultTenantProvider());

        var result = await service.SyncModelsAsync(providerId);

        // It must have hit the known OpenAI models endpoint.
        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://api.openai.com/v1/models");
        result.Added.Should().Be(2);
        result.Total.Should().Be(2);
        await modelRepo.Received(2).AddAsync(Arg.Any<Model>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncModelsAsync_GeminiOAuthAccount_UsesSharedEndpointAndBearerToken()
    {
        var providerId = Guid.NewGuid();
        var provider = CreateProviderWithCode(providerId, "Gemini Account 2", "gemini-acc2");
        provider.SetAuthMethod(AuthMethod.OAuth, Guid.NewGuid());

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetByIdAsync(providerId, ProviderAccountDashboardFacade.DefaultTenantId).Returns(provider);
        var oauth = Substitute.For<IOAuthFlowService>();
        oauth.GetValidAccessTokenAsync(providerId, Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("oauth-access-token");

        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetByProviderIdAsync(providerId, ProviderAccountDashboardFacade.DefaultTenantId, Arg.Any<CancellationToken>()).Returns(new List<Model>());
        var handler = new FakeHttpMessageHandler(req =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"object\":\"list\",\"data\":[{\"id\":\"gemini-2.5-flash\"}]}",
                    System.Text.Encoding.UTF8, "application/json")
            });
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient().Returns(new HttpClient(handler));

        var service = CreateService(providers: providerRepo, models: modelRepo,
            httpClientFactory: httpClientFactory, oauth: oauth,
            tenantProvider: DefaultTenantProvider());

        var result = await service.SyncModelsAsync(providerId);

        handler.LastRequest!.RequestUri!.ToString().Should()
            .Be("https://generativelanguage.googleapis.com/v1beta/openai/models");
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("oauth-access-token");
        result.Added.Should().Be(1);
        result.Total.Should().Be(1);
    }

    [Fact]
    public async Task SyncModelsAsync_GeminiOAuthAccount_ParsesNativeModelsResponse()
    {
        var providerId = Guid.NewGuid();
        var provider = CreateProviderWithCode(providerId, "Gemini Account 3", "gemini-acc3");
        provider.SetAuthMethod(AuthMethod.OAuth, Guid.NewGuid());
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetByIdAsync(providerId, ProviderAccountDashboardFacade.DefaultTenantId).Returns(provider);
        var oauth = Substitute.For<IOAuthFlowService>();
        oauth.GetValidAccessTokenAsync(providerId, Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("oauth-access-token");
        var modelRepo = Substitute.For<IModelRepository>();
        modelRepo.GetByProviderIdAsync(providerId, ProviderAccountDashboardFacade.DefaultTenantId, Arg.Any<CancellationToken>()).Returns(new List<Model>());
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"models\":[{\"name\":\"models/gemini-2.5-flash\",\"displayName\":\"Gemini 2.5 Flash\"}]}",
                System.Text.Encoding.UTF8, "application/json")
        });
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient().Returns(new HttpClient(handler));
        var service = CreateService(providers: providerRepo, models: modelRepo,
            httpClientFactory: httpClientFactory, oauth: oauth,
            tenantProvider: DefaultTenantProvider());

        var result = await service.SyncModelsAsync(providerId);

        result.Added.Should().Be(1);
        result.Total.Should().Be(1);
        await modelRepo.Received(1).AddAsync(
            Arg.Is<Model>(m => m.Code == "gemini-2.5-flash"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncModelsAsync_NullBaseUrl_UnknownCode_ThrowsActionableError()
    {
        // Regression: a provider with no BaseUrl and no known endpoint must surface a
        // clear error (so the UI alert shows it) rather than silently returning (0,0).
        var providerId = Guid.NewGuid();
        var provider = CreateProviderWithCode(providerId, "Mystery", "mystery");

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetByIdAsync(providerId, ProviderAccountDashboardFacade.DefaultTenantId).Returns(provider);

        var service = CreateService(
            providers: providerRepo,
            tenantProvider: DefaultTenantProvider());

        var act = async () => await service.SyncModelsAsync(providerId);
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("BaseUrl").And.Contain("Mystery");
    }

    [Fact]
    public async Task CreateOAuthAccountAsync_UsesLabelAsProviderName_NotAutoGenerated()
    {
        // Regression: the "Add Account" modal lets the operator type an Account
        // Label. That label must become the provider Name/display name, instead of
        // the auto-generated "Gemini Account 1". Code stays the stable "gemini-accN".
        var configId = Guid.NewGuid();
        var oauthCfg = Substitute.For<IOAuthProviderConfigRepository>();
        var existing = new List<AiProvider>(); // no prior accounts yet -> acc1

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetUsedCodesAsync(ProviderAccountDashboardFacade.DefaultTenantId).Returns(existing.Select(p => p.Code).ToHashSet());
        providerRepo.When(r => r.AddAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>()))
                    .Do(ci => existing.Add(ci.Arg<AiProvider>()));

        var modelRepo = Substitute.For<IModelRepository>();
        var service = CreateService(
            providers: providerRepo,
            oauthConfigs: oauthCfg,
            models: modelRepo,
            tenantProvider: DefaultTenantProvider());

        var code = await service.CreateOAuthAccountAsync(configId, "gemini", "Budi's Google");

        code.Should().Be("gemini-acc1");
        existing.Should().ContainSingle()
            .Which.Name.Should().Be("Budi's Google");
    }

    [Fact]
    public async Task CreateOAuthAccountAsync_EmptyLabel_FallsBackToAutoName()
    {
        // Programmatic / empty-label callers must still get a usable display name.
        var configId = Guid.NewGuid();
        var oauthCfg = Substitute.For<IOAuthProviderConfigRepository>();
        var existing = new List<AiProvider>();

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetUsedCodesAsync(ProviderAccountDashboardFacade.DefaultTenantId).Returns(existing.Select(p => p.Code).ToHashSet());
        providerRepo.When(r => r.AddAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>()))
                    .Do(ci => existing.Add(ci.Arg<AiProvider>()));

        var service = CreateService(
            providers: providerRepo,
            oauthConfigs: oauthCfg,
            tenantProvider: DefaultTenantProvider());

        var code = await service.CreateOAuthAccountAsync(configId, "gemini", "   ");

        code.Should().Be("gemini-acc1");
        existing.Should().ContainSingle()
            .Which.Name.Should().Be("Gemini Account 1");
    }

    [Fact]
    public async Task CreateOAuthAccountAsync_SkipsExistingAccountCodes()
    {
        var configId = Guid.NewGuid();
        var existingProvider = CreateProviderWithCode(Guid.NewGuid(), "Existing", "chatgpt-acc2");
        existingProvider.SetAuthMethod(AuthMethod.OAuth, configId);
        var existing = new List<AiProvider> { existingProvider };

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetUsedCodesAsync(ProviderAccountDashboardFacade.DefaultTenantId).Returns(existing.Select(p => p.Code).ToHashSet());
        providerRepo.When(r => r.AddAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>()))
            .Do(ci => existing.Add(ci.Arg<AiProvider>()));
        var modelRepo = Substitute.For<IModelRepository>();

        var service = CreateService(
            providers: providerRepo,
            models: modelRepo,
            tenantProvider: DefaultTenantProvider());

        var code = await service.CreateOAuthAccountAsync(configId, "chatgpt", "Second ChatGPT");

        code.Should().Be("chatgpt-acc1");
        existing.Should().Contain(p => p.Code == "chatgpt-acc1");
        await modelRepo.Received().AddAsync(
            Arg.Is<Model>(m => m.Code == "gpt-5.5"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateOAuthAccountAsync_SkipsLegacyProviderCodesAcrossAuthMethods()
    {
        var configId = Guid.NewGuid();
        var legacyProvider = CreateProviderWithCode(Guid.NewGuid(), "Legacy Gemini", "gemini-acc1");
        var linkedProvider = CreateProviderWithCode(Guid.NewGuid(), "Linked Gemini", "gemini-acc2");
        linkedProvider.SetAuthMethod(AuthMethod.OAuth, configId);
        var existing = new List<AiProvider> { legacyProvider, linkedProvider };

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetUsedCodesAsync(ProviderAccountDashboardFacade.DefaultTenantId).Returns(existing.Select(p => p.Code).ToHashSet());
        providerRepo.When(r => r.AddAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>()))
            .Do(ci => existing.Add(ci.Arg<AiProvider>()));
        var modelRepo = Substitute.For<IModelRepository>();

        var service = CreateService(
            providers: providerRepo,
            models: modelRepo,
            tenantProvider: DefaultTenantProvider());

        var code = await service.CreateOAuthAccountAsync(configId, "gemini", "Third Gemini");

        code.Should().Be("gemini-acc3");
    }

    [Fact]
    public async Task CreateOAuthAccountAsync_CreatesNativeProviderAccountProjection()
    {
        var configId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var existing = new List<AiProvider>();
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetUsedCodesAsync(tenantId).Returns(existing.Select(p => p.Code).ToHashSet());
        providerRepo.When(r => r.AddAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>()))
            .Do(ci => existing.Add(ci.Arg<AiProvider>()));

        var accountRepo = Substitute.For<IProviderAccountRepository>();
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(tenantId);
        var modelRepo = Substitute.For<IModelRepository>();

        var service = CreateService(
            providers: providerRepo,
            models: modelRepo,
            providerAccounts: accountRepo,
            tenantProvider: tenantProvider);

        var code = await service.CreateOAuthAccountAsync(configId, "gemini", "Native Gemini");

        code.Should().Be("gemini-acc1");
        var provider = existing.Should().ContainSingle().Which;
        await accountRepo.Received(1).AddAsync(
            Arg.Is<ProviderAccount>(account =>
                account.TenantId == tenantId
                && account.AiProviderId == provider.Id
                && account.Code == "gemini-acc1"
                && account.DisplayName == "Native Gemini"
                && account.AuthOwnership == ProviderAccountAuthOwnership.GatewayManagedOAuth
                && account.BrokerKind == null
                && account.SupportedModels == "gemini-2.0-flash-acc1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateOAuthAccountAsync_ChatGptUsesCodexModel_NotGeminiModel()
    {
        var configId = Guid.NewGuid();
        var existing = new List<AiProvider>();
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetUsedCodesAsync(ProviderAccountDashboardFacade.DefaultTenantId).Returns(existing.Select(p => p.Code).ToHashSet());
        providerRepo.When(r => r.AddAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>()))
            .Do(ci => existing.Add(ci.Arg<AiProvider>()));
        var modelRepo = Substitute.For<IModelRepository>();
        var addedModels = new List<Model>();
        modelRepo.When(r => r.AddAsync(Arg.Any<Model>(), Arg.Any<CancellationToken>()))
            .Do(ci => addedModels.Add(ci.Arg<Model>()));

        var service = CreateService(
            providers: providerRepo,
            models: modelRepo,
            tenantProvider: DefaultTenantProvider());

        await service.CreateOAuthAccountAsync(configId, "chatgpt", "Codex Account");

        addedModels.Should().ContainSingle().Which.Code.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task CreateOAuthAccountAsync_AllocatesCodesWithinCurrentTenantOnly()
    {
        var tenantId = Guid.NewGuid();
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetUsedCodesAsync(tenantId)
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var added = new List<AiProvider>();
        providerRepo.When(r => r.AddAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>()))
            .Do(call => added.Add(call.Arg<AiProvider>()));
        var service = CreateService(
            providers: providerRepo,
            models: Substitute.For<IModelRepository>(),
            tenantProvider: TenantProvider(tenantId));

        var code = await service.CreateOAuthAccountAsync(Guid.NewGuid(), "gemini", "Tenant account");

        code.Should().Be("gemini-acc1");
        added.Should().ContainSingle().Which.TenantId.Should().Be(tenantId);
        await providerRepo.Received(1).GetUsedCodesAsync(tenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateProviderAsync_DuplicateCodeThrowsActionableError()
    {
        var duplicate = CreateProviderWithCode(Guid.NewGuid(), "Existing", "chatgpt-acc2");
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetByCodeAsync("chatgpt-acc2", ProviderAccountDashboardFacade.DefaultTenantId)
            .Returns(duplicate);
        var service = CreateService(
            providers: providerRepo,
            tenantProvider: DefaultTenantProvider());

        var act = () => service.CreateProviderAsync("New", "chatgpt-acc2", null, null, 5, 0, 0, null, "OAuth", Guid.NewGuid());

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("already in use").And.Contain("Existing");
    }

    [Fact]
    public async Task UpdateProviderAsync_DuplicateCodeThrowsActionableError()
    {
        var current = CreateProviderWithCode(Guid.NewGuid(), "Current", "current");
        var duplicate = CreateProviderWithCode(Guid.NewGuid(), "Existing", "chatgpt-acc2");
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetByCodeAsync("chatgpt-acc2", ProviderAccountDashboardFacade.DefaultTenantId)
            .Returns(duplicate);
        providerRepo.GetByIdAsync(current.Id, ProviderAccountDashboardFacade.DefaultTenantId)
            .Returns(current);
        var service = CreateService(
            providers: providerRepo,
            tenantProvider: DefaultTenantProvider());

        var act = () => service.UpdateProviderAsync(current.Id, "Current", "chatgpt-acc2", null, null, 5, 0, 0, null, "OAuth", Guid.NewGuid());

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("already in use").And.Contain("Existing");
    }

    [Fact]
    public async Task CreateProviderAsync_OAuthRequiresAnExistingConfig()
    {
        var providerRepo = Substitute.For<IAiProviderRepository>();
        var oauthConfigs = Substitute.For<IOAuthProviderConfigRepository>();
        var missingConfigId = Guid.NewGuid();
        oauthConfigs.GetByIdAsync(missingConfigId, Arg.Any<CancellationToken>())
            .Returns((OAuthProviderConfig?)null);
        var service = CreateService(
            providers: providerRepo,
            oauthConfigs: oauthConfigs,
            tenantProvider: DefaultTenantProvider());

        var act = () => service.CreateProviderAsync(
            "Gemini", "gemini-new", null, null, 5, 0, 0, null, "OAuth", missingConfigId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("OAuth configuration");
        await providerRepo.DidNotReceive().AddAsync(
            Arg.Any<AiProvider>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOAuthAccountsAsync_SurfacesProviderNameAsLabel()
    {
        // The account list must render the human-readable Name (operator label),
        // not fall back to the Code. Regression for the "null label" drop.
        var configId = Guid.NewGuid();
        var accountProvider = CreateProviderWithCode(Guid.NewGuid(), "Budi's Google", "gemini-acc1");
        accountProvider.SetAuthMethod(AuthMethod.OAuth, configId);

        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync(ProviderAccountDashboardFacade.DefaultTenantId)
            .Returns(new List<AiProvider> { accountProvider });

        var oauth = Substitute.For<IOAuthFlowService>();
        oauth.GetStatusAsync("gemini-acc1", Arg.Any<CancellationToken>())
             .Returns(new OAuthConnectionStatus("gemini-acc1", OAuthTokenStatus.Connected, null, true, null));

        var service = CreateService(
            providers: providerRepo,
            oauth: oauth,
            tenantProvider: DefaultTenantProvider());

        var accounts = await service.GetOAuthAccountsAsync(configId);

        accounts.Should().ContainSingle();
        accounts[0].Label.Should().Be("Budi's Google");
        accounts[0].ProviderCode.Should().Be("gemini-acc1");
    }
}
