namespace Arkana.Gateway.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using Arkana.Application.Features.Chat.Commands;
using Arkana.Application.Features.Chat.Queries;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Arkana.Gateway.Api.Endpoints;
using Arkana.Gateway.Api.Services;
using Arkana.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using NSubstitute;

public sealed class ChatEndpointsTests
{
    /// <summary>
    /// Creates a minimal WebApplication with MapChatEndpoints and default mocks
    /// for all dependencies that the chat endpoints require.
    /// Specific mocks passed as parameters override the defaults.
    /// </summary>
    private static async Task<WebApplication> CreateChatHost(
        IMediator? mediator = null,
        IModelRepository? modelRepo = null,
        IHttpClientFactory? httpClientFactory = null,
        ITokenTracker? tokenTracker = null,
        IRequestLogger? requestLogger = null,
        ActiveStreamCounter? streamCounter = null,
        IAiProviderRepository? providerRepo = null,
        IConfiguration? config = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // Register defaults for all chat endpoint dependencies
        builder.Services.AddSingleton(mediator ?? Substitute.For<IMediator>());
        builder.Services.AddSingleton(modelRepo ?? Substitute.For<IModelRepository>());
        builder.Services.AddSingleton(httpClientFactory ?? Substitute.For<IHttpClientFactory>());
        builder.Services.AddSingleton(tokenTracker ?? Substitute.For<ITokenTracker>());
        builder.Services.AddSingleton(requestLogger ?? Substitute.For<IRequestLogger>());
        builder.Services.AddSingleton(streamCounter ?? new ActiveStreamCounter());
        // ChatEndpoints also depends on IAiProviderRepository and IConfiguration.
        builder.Services.AddSingleton(providerRepo ?? Substitute.For<IAiProviderRepository>());
        builder.Services.AddSingleton(config ?? new ConfigurationBuilder().Build());
        var tenantProvider = Substitute.For<ITenantProvider>();
        var tenantId = Guid.NewGuid();
        tenantProvider.TenantId.Returns(tenantId);
        if (modelRepo is not null)
        {
            modelRepo.GetAllAsync(tenantId, Arg.Any<CancellationToken>())
                .Returns(call => modelRepo.GetAllAsync(call.Arg<CancellationToken>()));
        }
        builder.Services.AddSingleton<ITenantProvider>(tenantProvider);

        // SECURITY: chat endpoints decrypt provider credentials via ICredentialVault.
        // Register a test vault so the endpoint lambdas can resolve it.
        builder.Services.AddSingleton<ICredentialVault>(TestVaultFactory.Create());
        // Chat streaming path also reads the provider list directly; provide a default mock.
        builder.Services.AddSingleton(Substitute.For<IAiProviderRepository>());

        builder.Services.AddLogging();

        var app = builder.Build();
        app.MapChatEndpoints();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task GetModels_ShouldReturnOk()
    {
        // Use EF Core InMemory to properly set up the Provider navigation property
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        await using (var db = new GatewayDbContext(options))
        {
            var provider = AiProvider.Create("OpenCode", "opencode", 0);
            db.AiProviders.Add(provider);
            await db.SaveChangesAsync();

            var model = Model.Create(provider.Id, "DeepSeek V4 Flash", "deepseek-v4-flash");
            db.Models.Add(model);
            await db.SaveChangesAsync();
        }

        // Reload with Include to get Provider navigation populated
        await using (var db = new GatewayDbContext(options))
        {
            var models = await db.Models.Include(m => m.Provider).ToListAsync();

            var modelRepo = Substitute.For<IModelRepository>();
            modelRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(models);

            using var host = await CreateChatHost(modelRepo: modelRepo);
            var client = host.GetTestClient();

            var response = await client.GetAsync("/v1/models");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = await response.Content.ReadAsStringAsync();
            json.Should().NotBeNullOrEmpty();
        }
    }

    /// <summary>
    /// Regression: /v1/models must not advertise models whose PROVIDER is
    /// disabled. Before 2026-08-06 the endpoint returned every row in the
    /// table, so agents were offered openai/anthropic models that had no
    /// credential and could never serve a request.
    /// </summary>
    [Fact]
    public async Task GetModels_ShouldExcludeModelsOfDisabledProviders()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        await using (var db = new GatewayDbContext(options))
        {
            var live = AiProvider.Create("Ollama", "ollama", 1);
            var dead = AiProvider.Create("OpenAI", "openai", 2);
            dead.Disable();
            db.AiProviders.AddRange(live, dead);
            await db.SaveChangesAsync();

            db.Models.Add(Model.Create(live.Id, "Qwen", "qwen2.5:7b-instruct"));
            db.Models.Add(Model.Create(dead.Id, "GPT-4o mini", "gpt-4o-mini"));
            await db.SaveChangesAsync();
        }

        await using (var db = new GatewayDbContext(options))
        {
            var models = await db.Models.Include(m => m.Provider).ToListAsync();
            var modelRepo = Substitute.For<IModelRepository>();
            modelRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(models);

            using var host = await CreateChatHost(modelRepo: modelRepo);
            var json = await host.GetTestClient().GetStringAsync("/v1/models");

            json.Should().Contain("qwen2.5:7b-instruct");
            json.Should().NotContain("gpt-4o-mini");
        }
    }

    /// <summary>
    /// Regression: a key restricted to specific models must only SEE those
    /// models. Previously the catalog was returned unfiltered and the
    /// restriction only surfaced as a 403 at call time.
    /// </summary>
    [Fact]
    public async Task GetModels_ShouldScopeToApiKeyAllowedModels()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        Guid allowedId;
        await using (var db = new GatewayDbContext(options))
        {
            var provider = AiProvider.Create("Ollama", "ollama", 1);
            db.AiProviders.Add(provider);
            await db.SaveChangesAsync();

            var allowed = Model.Create(provider.Id, "Qwen", "qwen2.5:7b-instruct");
            var hidden = Model.Create(provider.Id, "Other", "some-other-model");
            db.Models.AddRange(allowed, hidden);
            await db.SaveChangesAsync();
            allowedId = allowed.Id;
        }

        await using (var db = new GatewayDbContext(options))
        {
            var models = await db.Models.Include(m => m.Provider).ToListAsync();
            var modelRepo = Substitute.For<IModelRepository>();
            modelRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(models);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(Substitute.For<IMediator>());
            builder.Services.AddSingleton(modelRepo);
            builder.Services.AddSingleton(Substitute.For<IHttpClientFactory>());
            builder.Services.AddSingleton(Substitute.For<ITokenTracker>());
            builder.Services.AddSingleton(Substitute.For<IRequestLogger>());
            builder.Services.AddSingleton(new ActiveStreamCounter());
            builder.Services.AddSingleton(Substitute.For<IAiProviderRepository>());
            builder.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            builder.Services.AddSingleton<ICredentialVault>(TestVaultFactory.Create());
            var tenantProvider = Substitute.For<ITenantProvider>();
            var tenantId = Guid.NewGuid();
            tenantProvider.TenantId.Returns(tenantId);
            modelRepo.GetAllAsync(tenantId, Arg.Any<CancellationToken>())
                .Returns(call => modelRepo.GetAllAsync(call.Arg<CancellationToken>()));
            builder.Services.AddSingleton<ITenantProvider>(tenantProvider);
            builder.Services.AddLogging();

            var app = builder.Build();
            // Stand in for ApiKeyAuthMiddleware, which populates this item.
            app.Use(async (ctx, next) =>
            {
                ctx.Items["ApiKeyModelIds"] = new[] { allowedId };
                await next();
            });
            app.MapChatEndpoints();
            await app.StartAsync();
            using var _ = app;

            var json = await app.GetTestClient().GetStringAsync("/v1/models");

            json.Should().Contain("qwen2.5:7b-instruct");
            json.Should().NotContain("some-other-model");
        }
    }

    [Fact]
    public async Task GetTokenUsage_ShouldReturnOk()
    {
        var summary = new TokenUsageSummary
        {
            TotalRequests = 10,
            TotalTokens = 1000,
            TotalCost = 0.05m,
            ByProvider = new List<ProviderUsage>
            {
                new() { Provider = "OpenCode", Requests = 10, Tokens = 1000, Cost = 0.05m },
            },
        };

        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetTokenUsageQuery>(), Arg.Any<CancellationToken>())
            .Returns(summary);

        using var host = await CreateChatHost(mediator: mediator);
        var client = host.GetTestClient();

        // Pass explicit query params because DateTimeOffset binding from empty query fails
        var response = await client.GetAsync(
            "/v1/tokens/usage?from=2026-01-01T00:00:00Z&to=2026-12-31T23:59:59Z");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PostChatCompletion_NonStreaming_ShouldReturnOk()
    {
        var result = new SendChatResult
        {
            Content = "Hello, I am an AI assistant.",
            Provider = "OpenCode",
            Model = "deepseek-v4-flash",
            InputTokens = 10,
            OutputTokens = 20,
            EstimatedCost = 0.0001m,
            DurationMs = 500,
        };

        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SendChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(result);

        using var host = await CreateChatHost(mediator: mediator);
        var client = host.GetTestClient();

        var requestBody = new
        {
            model = "deepseek-v4-flash",
            messages = new[]
            {
                new { role = "user", content = "Hello" },
            },
            maxTokens = 100,
            temperature = 0.7,
            // stream not set (defaults to null = non-streaming)
        };

        var response = await client.PostAsJsonAsync("/v1/chat/completions", requestBody);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // Regression guard: a normal client (no MITM agent header) must be completely
        // unaffected. (Logging attribution is covered at the handler level, where the
        // real SendChatHandler runs — here the mediator is mocked so no log is written.)
    }

    [Fact]
    public async Task PostChatCompletion_WithMitmHeader_ShouldStillSucceed()
    {
        // The X-Via-Mitm-Agent header must never break a direct API client; it is
        // audit attribution only. Logging propagation is asserted at the handler level.
        var result = new SendChatResult
        {
            Content = "ok",
            Provider = "OpenCode",
            Model = "deepseek-v4-flash",
            InputTokens = 1,
            OutputTokens = 1,
            EstimatedCost = 0.0001m,
            DurationMs = 10,
        };

        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SendChatCommand>(), Arg.Any<CancellationToken>()).Returns(result);

        using var host = await CreateChatHost(mediator: mediator);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Via-Mitm-Agent", "antigravity");

        var requestBody = new
        {
            model = "deepseek-v4-flash",
            messages = new[] { new { role = "user", content = "Hello" } },
        };

        var response = await client.PostAsJsonAsync("/v1/chat/completions", requestBody);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
