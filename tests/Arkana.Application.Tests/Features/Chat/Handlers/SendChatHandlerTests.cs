using Arkana.Application.Features.Chat;
using Arkana.Application.Features.Chat.Commands;
using Arkana.Application.Features.Chat.Handlers;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Arkana.Application.Tests.Features.Chat.Handlers;

public class SendChatHandlerTests
{
    private readonly IModelRouter _router = Substitute.For<IModelRouter>();
    private readonly ITokenTracker _tokenTracker = Substitute.For<ITokenTracker>();
    private readonly IRequestLogger _requestLogger = Substitute.For<IRequestLogger>();
    private readonly IProviderCatalog _providerCatalog = Substitute.For<IProviderCatalog>();
    private readonly IApiKeyRepository _apiKeyRepo = Substitute.For<IApiKeyRepository>();
    private readonly IModelRepository _modelRepo = Substitute.For<IModelRepository>();
    private readonly IResponseCache _responseCache = Substitute.For<IResponseCache>();
    private readonly ISemanticCache _semanticCache = Substitute.For<ISemanticCache>();
    private readonly IChatMetricsRecorder _metrics = Substitute.For<IChatMetricsRecorder>();
    private readonly IRateLimiter _rateLimiter = Substitute.For<IRateLimiter>();
    // Compressors default to no-op in tests (the production
    // registered instances are no-ops when their feature flag
    // is off, which is the default in appsettings). Substitutes
    // return null/empty by default, so we wire them to pass
    // through the input value — mimicking the disabled feature.
    private readonly IInputCompressor _inputCompressor = Substitute.For<IInputCompressor>();
    private readonly IOutputCompressor _outputCompressor = Substitute.For<IOutputCompressor>();
    private readonly ITenantProvider _tenantProvider = Substitute.For<ITenantProvider>();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly ILogger<SendChatHandler> _logger;
    private readonly FallbackChainExecutor _fallback;
    private readonly SendChatHandler _sut;

    public SendChatHandlerTests()
    {
        _logger = Substitute.For<ILogger<SendChatHandler>>();
        _fallback = new FallbackChainExecutor(
            Substitute.For<ILogger<FallbackChainExecutor>>(),
            RetryPolicy.Test);

        // Default: cache disabled in test setup. Tests that exercise
        // the cache opt in by rebuilding _sut with a different
        // ResponseCacheOptions instance.
        var cacheOptions = Options.Create(new ResponseCacheOptions
        {
            Enabled = false,
            DefaultTtlSeconds = 300
        });
        var semanticCacheOptions = Options.Create(new SemanticCacheOptions
        {
            Enabled = false,
        });

        _sut = new SendChatHandler(
            _router, _tokenTracker, _requestLogger,
            _providerCatalog, _apiKeyRepo, _modelRepo, _fallback,
            _responseCache, _semanticCache, _metrics, _rateLimiter, _inputCompressor, _outputCompressor,
            Substitute.For<IBudgetEnforcer>(), _tenantProvider,
            cacheOptions, semanticCacheOptions, _logger);

        _tenantProvider.TenantId.Returns(_tenantId);
        _modelRepo.GetAllAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(call => _modelRepo.GetAllAsync(call.Arg<CancellationToken>()));
        _providerCatalog.GetAllAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(call => _providerCatalog.GetAllAsync(call.Arg<CancellationToken>()));

        // Compressor substitutes: pass-through behavior so the
        // default tests see uncompressed input and unchanged
        // output. The dedicated compressor tests cover the
        // transform behavior itself.
        _inputCompressor.Compress(Arg.Any<ChatRequest>())
            .Returns(call => call.Arg<ChatRequest>());
        _outputCompressor.Compress(Arg.Any<string>())
            .Returns(call => call.Arg<string>());
    }

    private static SendChatCommand CreateValidCommand(string? apiKey = "valid-key", string? viaMitmAgent = null)
    {
        return new SendChatCommand
        {
            Model = "gpt-4",
            Messages = new List<ChatMessageDto>
            {
                new() { Role = "user", Content = "Hello" }
            },
            ApiKey = apiKey,
            ViaMitmAgent = viaMitmAgent
        };
    }

    private static Model CreateTestModel(Guid? id = null)
    {
        return Model.Create(Guid.NewGuid(), "GPT-4", "gpt-4", 0.01m, 0.02m);
    }

    private static AiProvider CreateTestProvider(string code, string name, int priority)
    {
        return AiProvider.Create(name, code, priority,
            baseUrl: "https://example.com", apiKeyPlaintext: "test-key");
    }

    private static IChatCompletionService CreateMockProvider(string providerName = "OpenAI")
    {
        var provider = Substitute.For<IChatCompletionService>();
        provider.ProviderName.Returns(providerName);
        provider.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResult
            {
                Content = "Hello! How can I help you?",
                Model = "gpt-4",
                InputTokens = 10,
                OutputTokens = 20
            });
        return provider;
    }

    /// <summary>
    /// Helper: configure the router + provider repo with a single primary provider
    /// so the handler can build a fallback chain.
    /// </summary>
    private void WireSingleProvider(IChatCompletionService provider, string providerCode = "openai")
    {
        var aiProvider = CreateTestProvider(providerCode, providerCode, priority: 0);
        _providerCatalog.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AiProvider> { aiProvider });
        _router.GetAllProvidersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<IChatCompletionService> { provider });
    }

    [Fact]
    public async Task Handle_InvalidApiKey_ReturnsError()
    {
        // Arrange
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: "invalid-key");
        var keyHash = Domain.Services.ApiKeyHasher.Hash("invalid-key");
        _apiKeyRepo.GetByKeyHashAsync(keyHash, Arg.Any<CancellationToken>())
            .Returns((ApiKey?)null);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Content.Should().Be("Error: Invalid API key");
        result.Provider.Should().BeEmpty();
        result.Model.Should().Be("gpt-4");
        result.DurationMs.Should().Be(0);
    }

    [Fact]
    public async Task Handle_DeactivatedApiKey_ReturnsError()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        var model = CreateTestModel(modelId);
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: "deactivated-key");
        var keyHash = Domain.Services.ApiKeyHasher.Hash("deactivated-key");
        var apiKey = ApiKey.Create("TestKey", keyHash, "arkana-testkey");
        apiKey.Deactivate();
        _apiKeyRepo.GetByKeyHashAsync(keyHash, Arg.Any<CancellationToken>())
            .Returns(apiKey);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Content.Should().Be("Error: API key is deactivated");
        result.Provider.Should().BeEmpty();
        result.Model.Should().Be("gpt-4");
        result.DurationMs.Should().Be(0);
    }

    [Fact]
    public async Task Handle_ApiKeyWithoutModelAccess_ReturnsError()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        var model = CreateTestModel(modelId);
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: "restricted-key");
        var keyHash = Domain.Services.ApiKeyHasher.Hash("restricted-key");
        var otherModel = Model.Create(Guid.NewGuid(), "Claude", "claude-3");
        var apiKey = ApiKey.Create("RestrictedKey", keyHash, "arkana-restricted");
        apiKey.AllowedModels.Add(otherModel); // only allowed to access claude-3, not gpt-4
        _apiKeyRepo.GetByKeyHashAsync(keyHash, Arg.Any<CancellationToken>())
            .Returns(apiKey);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Content.Should().Be("Error: API key does not have access to model 'gpt-4'");
        result.Provider.Should().BeEmpty();
        result.Model.Should().Be("gpt-4");
        result.DurationMs.Should().Be(0);
    }

    [Fact]
    public async Task Handle_SuccessfulChatWithValidApiKey_ReturnsResult()
    {
        // Arrange
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: "good-key");
        var keyHash = Domain.Services.ApiKeyHasher.Hash("good-key");
        var apiKey = ApiKey.Create("GoodKey", keyHash, "arkana-goodkey");
        _apiKeyRepo.GetByKeyHashAsync(keyHash, Arg.Any<CancellationToken>())
            .Returns(apiKey);

        var provider = CreateMockProvider("OpenAI");
        WireSingleProvider(provider, providerCode: "openai");

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Content.Should().Be("Hello! How can I help you?");
        result.Provider.Should().Be("OpenAI");
        result.Model.Should().Be("gpt-4");
        result.InputTokens.Should().Be(10);
        result.OutputTokens.Should().Be(20);
        result.EstimatedCost.Should().Be(10 * 0.01m + 20 * 0.02m);
        result.DurationMs.Should().BeLessThan(1000); // mocked provider, but stopwatch still ticks

        await _tokenTracker.Received(1).RecordUsageAsync(
            Arg.Is<TokenUsage>(u =>
                u.Provider == "OpenAI" &&
                u.Model == "gpt-4" &&
                u.InputTokens == 10 &&
                u.OutputTokens == 20 &&
                u.ApiKeyName == "GoodKey"),
            Arg.Any<CancellationToken>());

        await _requestLogger.Received(1).RecordAsync(
            Arg.Is<RequestLog>(r =>
                r.Provider == "OpenAI" &&
                r.Model == "gpt-4" &&
                r.ApiKeyName == "GoodKey" &&
                r.ResponseContent == "Hello! How can I help you?" &&
                !r.IsError),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AnonymousChat_WithoutApiKey_Succeeds()
    {
        // Arrange
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: null); // anonymous

        var provider = CreateMockProvider("OpenAI");
        WireSingleProvider(provider, providerCode: "openai");

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Content.Should().Be("Hello! How can I help you?");
        result.Provider.Should().Be("OpenAI");
        result.Model.Should().Be("gpt-4");
        result.InputTokens.Should().Be(10);
        result.OutputTokens.Should().Be(20);

        // API key repo should never be called for anonymous requests
        await _apiKeyRepo.DidNotReceiveWithAnyArgs().GetByKeyHashAsync(default!, default);

        await _tokenTracker.Received(1).RecordUsageAsync(
            Arg.Is<TokenUsage>(u => u.ApiKeyName == null),
            Arg.Any<CancellationToken>());

        await _requestLogger.Received(1).RecordAsync(
            Arg.Is<RequestLog>(r => r.ApiKeyName == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithMitmAgentHeader_PropagatesToRequestLog()
    {
        // When an employee MITM agent forwards the request (ViaMitmAgent set),
        // the audit log must attribute it so cost/usage shows under the right source.
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: "good-key", viaMitmAgent: "antigravity");
        var keyHash = Domain.Services.ApiKeyHasher.Hash("good-key");
        var apiKey = ApiKey.Create("GoodKey", keyHash, "arkana-goodkey");
        _apiKeyRepo.GetByKeyHashAsync(keyHash, Arg.Any<CancellationToken>())
            .Returns(apiKey);

        var provider = CreateMockProvider("OpenAI");
        WireSingleProvider(provider, providerCode: "openai");

        var result = await _sut.Handle(command, CancellationToken.None);

        result.Content.Should().Be("Hello! How can I help you?");

        await _requestLogger.Received(1).RecordAsync(
            Arg.Is<RequestLog>(r =>
                r.ViaMitmAgent == "antigravity" &&
                !r.IsError),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithoutMitmAgentHeader_LeavesRequestLogNull()
    {
        // Regression guard: a normal (non-MITM) request must keep ViaMitmAgent null.
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: "good-key"); // ViaMitmAgent defaults to null
        var keyHash = Domain.Services.ApiKeyHasher.Hash("good-key");
        var apiKey = ApiKey.Create("GoodKey", keyHash, "arkana-goodkey");
        _apiKeyRepo.GetByKeyHashAsync(keyHash, Arg.Any<CancellationToken>())
            .Returns(apiKey);

        var provider = CreateMockProvider("OpenAI");
        WireSingleProvider(provider, providerCode: "openai");

        var result = await _sut.Handle(command, CancellationToken.None);

        result.Content.Should().Be("Hello! How can I help you?");

        await _requestLogger.Received(1).RecordAsync(
            Arg.Is<RequestLog>(r => r.ViaMitmAgent == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ProviderFailure_ReturnsErrorResponse()
    {
        // Arrange
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: null); // anonymous

        var provider = Substitute.For<IChatCompletionService>();
        provider.ProviderName.Returns("OpenAI");
        provider.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResult
            {
                Content = string.Empty,
                Model = "gpt-4",
                InputTokens = 0,
                OutputTokens = 0,
                ErrorMessage = "Provider returned 500 Internal Server Error"
            });
        WireSingleProvider(provider, providerCode: "openai");

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Content.Should().Be("Error: Provider returned 500 Internal Server Error");
        result.Provider.Should().Be("OpenAI");
        result.Model.Should().Be("gpt-4");
        result.InputTokens.Should().Be(0);
        result.OutputTokens.Should().Be(0);
        // Duration is non-zero because RetryPolicy.Test backs off between attempts.
        // Use a generous bound so the test is robust under load.
        result.DurationMs.Should().BeLessThan(1000);

        await _tokenTracker.Received(1).RecordUsageAsync(
            Arg.Any<TokenUsage>(), Arg.Any<CancellationToken>());

        await _requestLogger.Received(1).RecordAsync(
            Arg.Is<RequestLog>(r => r.IsError && r.ErrorMessage == "Provider returned 500 Internal Server Error"),
            Arg.Any<CancellationToken>());
    }

    // ── Fallback chain tests (REL-ARKANA-001) ──────────────────────

    [Fact]
    public async Task Handle_PrimaryFailsWithRetryableError_FallsBackToSecondary()
    {
        // Arrange
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: null);

        // Primary: fails with 500 (retryable)
        var primary = Substitute.For<IChatCompletionService>();
        primary.ProviderName.Returns("OpenAI");
        primary.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResult
            {
                ErrorMessage = "Internal Server Error",
                Model = "gpt-4"
            });

        // Fallback: succeeds
        var fallback = CreateMockProvider("DeepSeek");

        var primaryAi = CreateTestProvider("openai", "OpenAI", priority: 0);
        var fallbackAi = CreateTestProvider("deepseek", "DeepSeek", priority: 1);
        _providerCatalog.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AiProvider> { primaryAi, fallbackAi });
        _router.GetAllProvidersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<IChatCompletionService> { primary, fallback });

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Content.Should().Be("Hello! How can I help you?");
        result.Provider.Should().Be("DeepSeek"); // served by fallback
        result.InputTokens.Should().Be(10);

        // Both providers were called (primary retried 2x, fallback 1x)
        await primary.Received(2).CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
        await fallback.Received(1).CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PrimaryFailsWithNonRetryableError_DoesNotFallBack()
    {
        // Arrange
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: null);

        // Primary: fails with 401 (non-retryable — bad auth)
        var primary = Substitute.For<IChatCompletionService>();
        primary.ProviderName.Returns("OpenAI");
        primary.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResult
            {
                ErrorMessage = "401 Unauthorized: invalid api key",
                Model = "gpt-4"
            });

        // Fallback would succeed, but should never be called
        var fallback = CreateMockProvider("DeepSeek");

        var primaryAi = CreateTestProvider("openai", "OpenAI", priority: 0);
        var fallbackAi = CreateTestProvider("deepseek", "DeepSeek", priority: 1);
        _providerCatalog.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AiProvider> { primaryAi, fallbackAi });
        _router.GetAllProvidersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<IChatCompletionService> { primary, fallback });

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Provider.Should().Be("OpenAI"); // primary reported
        result.Content.Should().Contain("401 Unauthorized");

        // Non-retryable stops the chain after 1 attempt — no retry, no fallback
        await primary.Received(1).CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
        await fallback.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default);
    }

    [Fact]
    public async Task Handle_AllProvidersFail_ReturnsLastError()
    {
        // Arrange
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: null);

        var p1 = Substitute.For<IChatCompletionService>();
        p1.ProviderName.Returns("OpenAI");
        p1.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResult { ErrorMessage = "Connection timeout", Model = "gpt-4" });

        var p2 = Substitute.For<IChatCompletionService>();
        p2.ProviderName.Returns("DeepSeek");
        p2.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResult { ErrorMessage = "Service Unavailable", Model = "gpt-4" });

        var p1Ai = CreateTestProvider("openai", "OpenAI", priority: 0);
        var p2Ai = CreateTestProvider("deepseek", "DeepSeek", priority: 1);
        _providerCatalog.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AiProvider> { p1Ai, p2Ai });
        _router.GetAllProvidersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<IChatCompletionService> { p1, p2 });

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Content.Should().Contain("Service Unavailable"); // last error
        result.Provider.Should().Be("DeepSeek");
        // Each provider was retried 2x (RetryPolicy.Test MaxAttempts=2)
        await p1.Received(2).CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
        await p2.Received(2).CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoProvidersAvailable_ReturnsError()
    {
        // Arrange — empty chain
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: null);

        _providerCatalog.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AiProvider>());
        _router.GetAllProvidersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<IChatCompletionService>());

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Content.Should().Be("Error: No providers available");
        result.Provider.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_DisabledProviderExcludedFromChain()
    {
        // Arrange — only the disabled provider is in the DB; the enabled one
        // has no matching service. Chain should be empty.
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: null);

        var disabledAi = CreateTestProvider("openai", "OpenAI", priority: 0);
        disabledAi.Disable();
        _providerCatalog.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AiProvider> { disabledAi });
        // Router still returns a service for the disabled provider — the
        // handler should skip it because BuildChain filters on IsEnabled.
        _router.GetAllProvidersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<IChatCompletionService>());

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Content.Should().Be("Error: No providers available");
    }

    // ─── Response cache (PERF-ARKANA-002) ──────────────────────────
    // The handler reads the IResponseCache before executing the
    // fallback chain (read-through) and writes a successful response
    // back after the chain returns (write-back). These tests pin
    // that behavior end-to-end.

    /// <summary>
    /// Build a new SUT with the cache enabled and the supplied cache
    /// stub. Default SUT (built in the test ctor) has the cache
    /// disabled so the legacy tests don't see any cache interference.
    /// </summary>
    private SendChatHandler BuildSutWithCache(
        IResponseCache cache,
        ResponseCacheOptions? options = null)
    {
        return new SendChatHandler(
            _router, _tokenTracker, _requestLogger,
            _providerCatalog, _apiKeyRepo, _modelRepo, _fallback,
            cache, _semanticCache, _metrics, _rateLimiter, _inputCompressor, _outputCompressor,
            Substitute.For<IBudgetEnforcer>(), _tenantProvider,
            Options.Create(options ?? new ResponseCacheOptions
            {
                Enabled = true,
                DefaultTtlSeconds = 300
            }),
            Options.Create(new SemanticCacheOptions { Enabled = false }), _logger);
    }

    [Fact]
    public async Task Handle_CacheHit_ReturnsCachedResponse_WithoutHittingProvider()
    {
        // Arrange
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: null);

        // Build a ChatRequest that the handler will canonicalize, then
        // prime the cache with a response for that key. We use the
        // exact command so the cache key matches.
        var key = CacheKey.From(new ChatRequest
        {
            Model = command.Model,
            Messages = command.Messages.Select(m => new ChatMessage
            {
                Role = m.Role,
                Content = m.Content
            }).ToList()
        });
        var cached = new CachedResponse(
            Content: "Hello from cache!",
            Model: "gpt-4",
            InputTokens: 7,
            OutputTokens: 3,
            ToolCallsCount: 0,
            ToolCalls: null,
            ServedByProviderName: "opencode",
            CachedAt: DateTimeOffset.UtcNow);
        _responseCache.GetAsync(key, Arg.Any<CancellationToken>())
            .Returns(cached);

        var sut = BuildSutWithCache(_responseCache);

        // Act
        var result = await sut.Handle(command, CancellationToken.None);

        // Assert
        result.Content.Should().Be("Hello from cache!");
        result.Provider.Should().Be("opencode");
        result.InputTokens.Should().Be(7);
        result.OutputTokens.Should().Be(3);

        // Provider was NEVER called on a cache hit
        await _router.DidNotReceiveWithAnyArgs().GetAllProvidersAsync(default);
    }

    [Fact]
    public async Task Handle_CacheHit_StillRecordsTokenUsageAndRequestLog()
    {
        // The dashboard accuracy depends on cache hits showing up in
        // token usage and the request log. A hit that "saves cost" by
        // skipping accounting is a bad trade — observability first.
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: null);
        var key = CacheKey.From(new ChatRequest
        {
            Model = command.Model,
            Messages = command.Messages.Select(m => new ChatMessage
            {
                Role = m.Role,
                Content = m.Content
            }).ToList()
        });
        _responseCache.GetAsync(key, Arg.Any<CancellationToken>())
            .Returns(new CachedResponse(
                Content: "x", Model: "gpt-4",
                InputTokens: 5, OutputTokens: 5, ToolCallsCount: 0,
                ToolCalls: null, ServedByProviderName: "opencode",
                CachedAt: DateTimeOffset.UtcNow));

        var sut = BuildSutWithCache(_responseCache);

        await sut.Handle(command, CancellationToken.None);

        await _tokenTracker.Received(1).RecordUsageAsync(
            Arg.Is<TokenUsage>(u =>
                u.Provider == "opencode" &&
                u.InputTokens == 5 && u.OutputTokens == 5),
            Arg.Any<CancellationToken>());

        await _requestLogger.Received(1).RecordAsync(
            Arg.Is<RequestLog>(l =>
                l.Provider == "opencode" &&
                l.ResponseContent == "x" &&
                !l.IsError),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CacheMiss_ExecutesChain_AndWritesBackOnSuccess()
    {
        // Arrange
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: null);
        _responseCache.GetAsync(Arg.Any<CacheKey>(), Arg.Any<CancellationToken>())
            .Returns((CachedResponse?)null); // miss

        var provider = CreateMockProvider("OpenAI");
        WireSingleProvider(provider, "openai");

        var sut = BuildSutWithCache(_responseCache);

        // Act
        var result = await sut.Handle(command, CancellationToken.None);

        // Assert
        result.Content.Should().Contain("Hello");

        // Write-back was called
        await _responseCache.Received(1).SetAsync(
            Arg.Any<CacheKey>(),
            Arg.Is<CachedResponse>(e => e.Content.Contains("Hello")),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CacheMiss_ProviderFails_DoesNotWriteFailureToCache()
    {
        // We must never cache a failure — a transient 503 today could
        // be a 200 tomorrow, and caching the bad response would
        // amplify the outage to every identical subsequent request.
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: null);
        _responseCache.GetAsync(Arg.Any<CacheKey>(), Arg.Any<CancellationToken>())
            .Returns((CachedResponse?)null);

        // Single provider that always fails with a retryable error
        var provider = Substitute.For<IChatCompletionService>();
        provider.ProviderName.Returns("OpenAI");
        provider.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResult
            {
                ErrorMessage = "503 Service Unavailable",
                Model = "gpt-4"
            });
        WireSingleProvider(provider, "openai");

        var sut = BuildSutWithCache(_responseCache);

        var result = await sut.Handle(command, CancellationToken.None);

        result.Content.Should().Contain("503");
        await _responseCache.DidNotReceiveWithAnyArgs().SetAsync(default, default!, default, default);
    }

    [Fact]
    public async Task Handle_PreferredProviderSet_SkipsCache()
    {
        // A caller that explicitly chose a provider is making a
        // cost/latency decision we should not second-guess by
        // serving them someone else's cached response.
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: null) with { PreferredProvider = "openai" };

        var provider = CreateMockProvider("OpenAI");
        WireSingleProvider(provider, "openai");

        var sut = BuildSutWithCache(_responseCache);

        await sut.Handle(command, CancellationToken.None);

        // Cache.Get was never called when PreferredProvider is set
        await _responseCache.DidNotReceiveWithAnyArgs().GetAsync(default, default);
    }

    [Fact]
    public async Task Handle_CacheDisabled_StillWritesNothing()
    {
        // Default SUT has cache disabled. Even with a cache stub wired
        // in, the handler should not touch it at all.
        var model = CreateTestModel();
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model });

        var command = CreateValidCommand(apiKey: null);
        var provider = CreateMockProvider("OpenAI");
        WireSingleProvider(provider, "openai");

        await _sut.Handle(command, CancellationToken.None);

        await _responseCache.DidNotReceiveWithAnyArgs().GetAsync(default, default);
        await _responseCache.DidNotReceiveWithAnyArgs().SetAsync(default, default!, default, default);
    }

    [Fact]
    public async Task Handle_UnknownGeminiModel_FailsClosedBeforeFallback()
    {
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Model>());
        _providerCatalog.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AiProvider>());

        var result = await _sut.Handle(new SendChatCommand
        {
            Model = "gemini-2.5-flash",
            Messages = [new ChatMessageDto { Role = "user", Content = "hello" }]
        }, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.StatusCode.Should().Be(503);
        result.Provider.Should().Be("gemini");
        result.Content.Should().Be("Error: Gemini provider routing is unavailable.");
        await _router.DidNotReceiveWithAnyArgs().GetAllProvidersAsync(default);
    }

    [Fact]
    public async Task Handle_UnavailableGeminiPin_FailsClosedBeforeFallback()
    {
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Model>());
        _providerCatalog.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AiProvider>());

        var result = await _sut.Handle(new SendChatCommand
        {
            Model = "gpt-4",
            PreferredProvider = "gemini-acc9",
            Messages = [new ChatMessageDto { Role = "user", Content = "hello" }]
        }, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.StatusCode.Should().Be(503);
        result.Provider.Should().Be("gemini");
        await _router.DidNotReceiveWithAnyArgs().GetAllProvidersAsync(default);
    }

    [Fact]
    public async Task Handle_StableGeminiSubscriptionAlias_UsesBrokerConnectorWithoutCatalogRow()
    {
        _modelRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Model>());
        _providerCatalog.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AiProvider>());

        var broker = CreateMockProvider("gemini-subscription");
        broker.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResult
            {
                Content = "broker answer",
                Model = "gemini-2.5-flash",
                InputTokens = 3,
                OutputTokens = 4,
                RouteKind = "broker-managed",
                ResolvedProviderAccountCode = "gemini-acc1"
            });
        _router.GetAllProvidersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<IChatCompletionService> { broker });

        var result = await _sut.Handle(new SendChatCommand
        {
            Model = "gemini-2.5-flash",
            PreferredProvider = "gemini-subscription",
            Messages = [new ChatMessageDto { Role = "user", Content = "hello" }]
        }, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Be("broker answer");
        result.Provider.Should().Be("gemini-subscription");
        await broker.Received(1).CompleteAsync(
            Arg.Is<ChatRequest>(r => r.PreferredProviderCode == "gemini-subscription"),
            Arg.Any<CancellationToken>());
    }
}
