using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;
using Arkana.Infrastructure.AI;
using Arkana.Infrastructure.AI.Dialects;
using Arkana.Infrastructure.Mcp;
using Arkana.Infrastructure.Observability;
using Arkana.Infrastructure.OAuth;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Repositories;
using Arkana.Infrastructure.Security;
using Arkana.Infrastructure.Services;
using Arkana.Infrastructure.Broker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Arkana.Infrastructure;

/// <summary>
/// Configures infrastructure-layer services including EF Core, repositories,
/// token tracking, request logging, AI provider HTTP clients, the envelope-
/// encryption credential vault, and SSRF defense on outbound HTTP calls.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers all infrastructure services: DbContext, repositories, token tracker,
    /// request logger, AI provider HTTP clients (OpenCode, OpenAI, CLIProxyAPI,
    /// DeepSeek), the model router, the credential vault, and SSRF-safe handlers.
    /// </summary>
    /// <param name="dataDir">
    /// Directory for persistent gateway data (used to auto-generate the master key
    /// file when no explicit master key is configured). Defaults to <c>~/.arkana</c>.
    /// </param>
    /// <param name="ssrfAllowHttp">If true, allow http:// (default false). Local dev only.</param>
    /// <param name="ssrfAllowPrivate">
    /// If true, allow private network ranges (default false). Enable only for tests
    /// pointing at CLIProxyAPI on localhost.
    /// </param>
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        string postgresConnectionString,
        string? openCodeBaseUrl = null,
        string? redisConnectionString = null,
        string? dataDir = null,
        bool ssrfAllowHttp = false,
        bool ssrfAllowPrivate = false)
    {
        // Bind tolerantly: an empty or malformed value (e.g. a compose line whose env var is
        // unset, which interpolates to "") would otherwise throw out of the options binder and
        // take the whole gateway down on the first read — including the startup check that
        // exists to report exactly this misconfiguration. A failed bind leaves the option at its
        // default (Guid.Empty / no slots), so the feature stays disabled, every other surface
        // keeps serving, and GeminiBrokerConfigValidation names the key to fix.
        services.AddOptions<CLIProxyManagementOptions>().Configure<IConfiguration>((options, configuration) =>
            BindTolerantly(configuration.GetSection(CLIProxyManagementOptions.Section), options));
        services.AddOptions<GeminiSubscriptionOptions>().Configure<IConfiguration>((options, configuration) =>
            BindTolerantly(configuration.GetSection(GeminiSubscriptionOptions.Section), options));
        services.AddHttpClient("gemini-broker-management", client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient("gemini-broker-data-plane", client => client.Timeout = TimeSpan.FromMinutes(5));
        services.AddScoped<ICLIProxyManagementClientFactory, CLIProxyManagementClientFactory>();
        services.AddScoped<IProviderAccountSelector, ProviderAccountSelector>();
        services.AddScoped<IProviderAccountRepository, ProviderAccountRepository>();
        services.AddScoped<IProviderTargetPlanner, ProviderTargetPlanner>();
        services.AddScoped<IGeminiSubscriptionDataPlaneClient, GeminiSubscriptionDataPlaneClient>();
        services.AddScoped<IProviderAccountOperationRepository, ProviderAccountOperationRepository>();
        services.AddScoped<IChatCompletionService, GeminiSubscriptionChatService>();
        // EF Core — PostgreSQL. Both AddDbContext (for the existing
        // scoped consumers — repositories, scoped services) and
        // AddDbContextFactory (for PERF-ARKANA-005's batched metering
        // flusher, which is a singleton and needs to create fresh
        // contexts per flush so it doesn't share state with request
        // threads).
        services.AddDbContext<GatewayDbContext>(options =>
            options.UseNpgsql(postgresConnectionString));
        services.AddDbContextFactory<GatewayDbContext>(options =>
            options.UseNpgsql(postgresConnectionString), lifetime: ServiceLifetime.Singleton);

        // PERF-ARKANA-005: the DbContextFactory is registered as a Singleton (consumed by
        // the batched metering flusher, which is also a singleton). AddDbContext registers
        // DbContextOptions<GatewayDbContext> as Scoped, which makes the singleton factory
        // capture a scoped dependency and fail DI validation at startup. Promote the options
        // to a Singleton so both the scoped DbContext and the singleton factory resolve the
        // same (connection-string-only) options instance — no scoped dependency is captured.
        services.AddSingleton(serviceProvider =>
        {
            var optionsBuilder = new DbContextOptionsBuilder<GatewayDbContext>();
            optionsBuilder.UseNpgsql(postgresConnectionString);
            return optionsBuilder.Options;
        });

        // Repositories
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IAiProviderRepository, AiProviderRepository>();
        services.AddScoped<IModelRepository, ModelRepository>();
        services.AddScoped<IGlobalRateLimitRepository, GlobalRateLimitRepository>();
        services.AddScoped<IAccountUsageSnapshotRepository, AccountUsageSnapshotRepository>();
        services.AddScoped<IDashboardUserRepository, DashboardUserRepository>();
        services.AddScoped<IPolicyTemplateRepository, PolicyTemplateRepository>();

        // Token tracking — queue-backed (PERF-ARKANA-005).
        // RecordUsageAsync now enqueues; reads still go through the
        // DbContext factory.
        services.AddScoped<ITokenTracker, BatchedTokenTracker>();

        // Full request/response logging — queue-backed (PERF-ARKANA-005).
        // RecordAsync now enqueues; reads still go through the
        // DbContext factory.
        services.AddScoped<IRequestLogger, BatchedRequestLogger>();

        // ── Credential Vault (envelope encryption) ────────────────
        // Resolved once at app startup so the master key is loaded from env var,
        // file, or auto-generated (with a loud warning). The vault is a singleton
        // because (a) it holds a secret in memory and (b) the underlying state
        // is immutable after construction.
        services.AddSingleton<ICredentialVault>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<EnvelopeCredentialVault>>();
            var (key, source) = MasterKeyProvider.Resolve(dataDir, logger);
            return new EnvelopeCredentialVault(key);
        });

        // ── SSRF defense (SEC-ARKANA-004) ────────────────────────────────────
        // Singletons: the validator and DNS resolver are stateless / thread-safe.
        services.AddSingleton(_ => new UrlSafetyOptions
        {
            AllowHttp = ssrfAllowHttp,
            AllowPrivateAddresses = ssrfAllowPrivate
        });
        services.AddSingleton<DnsResolver>();
        services.AddSingleton<UrlSafetyValidator>();

        // SSRF-safe HTTP message handler — required by AddHttpMessageHandler<T>()
        // on every named HttpClient that makes outbound calls (SEC-ARKANA-004).
        services.AddTransient<SsrfSafeHttpHandler>();

        // ── Dialect translators (AI-ARKANA-004, task 14b-1) ───────────────
        // One OpenAI-compat translator serves all four OpenAI-shape providers
        // (OpenAI, OpenCode, DeepSeek, CLIProxyAPI). Adding a new dialect
        // (Anthropic, Gemini) is a single new line here plus a translator
        // implementation. The connector factory (task 14c) will map
        // AiProvider.Code -> IDialectTranslator at request time.
        services.AddSingleton<OpenAiDialectTranslator>();
        services.AddSingleton<IDialectTranslator>(sp => sp.GetRequiredService<OpenAiDialectTranslator>());
        services.AddSingleton<AnthropicDialectTranslator>();
        // IDialectTranslator is registered as a single concrete type per
        // dialect; the connector ctor takes its specific translator type
        // directly (see AnthropicChatService). The factory's
        // IEnumerable<IChatCompletionService> picks up every connector
        // automatically.
        services.AddSingleton<GeminiDialectTranslator>();

        // AI Providers — register as named HttpClient for each.
        // SECURITY: each provider service takes the ICredentialVault in its
        // constructor and decrypts the stored ApiKey just-in-time per request.
        // The SsrfSafeHttpHandler wraps the inner handler to validate every
        // outbound destination before the request is sent.
        services.AddHttpClient<IChatCompletionService, OpenCodeChatService>("opencode", client =>
        {
            client.BaseAddress = new Uri((openCodeBaseUrl ?? "https://opencode.ai/zen/go/v1").TrimEnd('/') + "/");
        })
        .AddHttpMessageHandler<SsrfSafeHttpHandler>();

        // Streaming HTTP client for SSE passthrough (used by ChatEndpoints streaming mode)
        services.AddHttpClient("opencode-streaming", client =>
        {
            client.BaseAddress = new Uri((openCodeBaseUrl ?? "https://opencode.ai/zen/go/v1").TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromMinutes(10); // Long timeout for streaming
        })
        .AddHttpMessageHandler<SsrfSafeHttpHandler>();

        // Streaming client for SELF-HOSTED upstreams (Ollama et al).
        //
        // Added 2026-08-06: "opencode-streaming" is wrapped in
        // SsrfSafeHttpHandler, which rejects plain-HTTP and private
        // destinations. A self-hosted upstream is exactly that, so
        // streaming from Ollama failed with
        //   "blocked by SSRF guard: HTTP is not allowed in this environment"
        // while non-streaming (which uses the connector's own HttpClient)
        // worked fine. Same reasoning as the Ollama connector registration:
        // the base URL is operator config, not user input.
        //
        // BaseAddress is assigned per-request by the endpoint, so none is
        // set here.
        services.AddHttpClient("local-streaming", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
        });

        // ── Embedding service (AI-ARKANA-006, task 18) ──────────
        services.AddSingleton<IEmbeddingService, EmbeddingService>();

        services.AddHttpClient<IChatCompletionService, OpenAIChatService>("openai", client =>
        {
            // Base URL is set per-request in the service; API key comes from the database (sealed).
        })
        .AddHttpMessageHandler<SsrfSafeHttpHandler>();

        services.AddHttpClient<IChatCompletionService, CLIProxyAPIChatService>("cliproxyapi", client =>
        {
            // CLIProxyAPI runs locally. Default port 12345; override via ProviderOptions:CLIProxyAPI:BaseUrl.
            // Base URL is set per-request in the service; API key comes from the database (sealed).
        })
        .AddHttpMessageHandler<SsrfSafeHttpHandler>();

        services.AddHttpClient<IChatCompletionService, DeepSeekChatService>("deepseek", client =>
        {
            // Base URL comes from the database (AiProviders table).
            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddHttpMessageHandler<SsrfSafeHttpHandler>();

        // Anthropic Messages API (task 15b) — separate dialect from
        // OpenAI. Auth header is x-api-key + anthropic-version, not
        // Authorization: Bearer. The connector sets those headers per
        // request; here we only configure the base address and timeout.
        services.AddHttpClient<IChatCompletionService, AnthropicChatService>("anthropic", client =>
        {
            client.BaseAddress = new Uri("https://api.anthropic.com/");
            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddHttpMessageHandler<SsrfSafeHttpHandler>();

        // Gemini API (task 15d) — auth via x-goog-api-key header.
        // Base URL defaults to https://generativelanguage.googleapis.com/v1beta/
        // which the server uses; the connector constructs the model-specific
        // endpoint path.
        services.AddHttpClient<IChatCompletionService, GeminiChatService>("gemini", client =>
        {
            client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/");
            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddHttpMessageHandler<SsrfSafeHttpHandler>();

        // Task 16: five more OpenAI-compat providers, all sharing
        // OpenAiDialectTranslator. Each is a thin subclass of
        // OpenAiCompatChatServiceBase that just sets the endpoint and
        // env-var fallback. The factory picks them up automatically
        // via IEnumerable<IChatCompletionService>. 5-minute timeout
        // mirrors DeepSeek. The SsrfSafeHttpHandler guards every
        // outbound URL.
        services.AddHttpClient<IChatCompletionService, GroqChatService>("groq", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddHttpMessageHandler<SsrfSafeHttpHandler>();

        // Ollama — self-hosted, independent fallback upstream (no API key,
        // no OAuth). Base address is configurable so the host can be
        // repointed without a recompile; default targets the sibling
        // container on the compose network.
        // NOTE: deliberately NOT wrapped in SsrfSafeHttpHandler — that
        // handler blocks private/loopback addresses, which is exactly
        // where a self-hosted upstream lives. This URL is operator-supplied
        // config, never user input, so it is not an SSRF vector.
        services.AddHttpClient<IChatCompletionService, OllamaChatService>("ollama", client =>
        {
            var baseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
                ?? "http://ollama:11434/v1/";
            if (!baseUrl.EndsWith('/')) baseUrl += "/";
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        services.AddHttpClient<IChatCompletionService, OpenRouterChatService>("openrouter", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddHttpMessageHandler<SsrfSafeHttpHandler>();

        services.AddHttpClient<IChatCompletionService, QwenChatService>("qwen", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddHttpMessageHandler<SsrfSafeHttpHandler>();

        services.AddHttpClient<IChatCompletionService, GLMChatService>("glm", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddHttpMessageHandler<SsrfSafeHttpHandler>();

        services.AddHttpClient<IChatCompletionService, CloudflareChatService>("cloudflare", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddHttpMessageHandler<SsrfSafeHttpHandler>();

        // Model Router — SCOPED, deliberately. ModelRouter constructor-injects
        // IEnumerable<IChatCompletionService>, which the container resolves eagerly
        // and captures for the injector's lifetime. Several connectors in that
        // enumerable are scoped (they depend on the request-scoped DbContext and
        // OAuth services). A singleton router therefore captured ONE DbContext for
        // the whole process: every request shared its change tracker (stale tracked
        // ProviderAccount snapshots survived across requests and broke the
        // provider-account version fencing with "Provider account changed"),
        // thread-safety violations were possible under concurrency, and the
        // captured ITenantProvider leaked per-request tenant state between tenants.
        // Scoped matches the connector factory below and keeps the graph per-request.
        services.AddScoped<IModelRouter, ModelRouter>();

        // Provider connector factory (AI-ARKANA-004, task 14c). The factory
        // receives every registered IChatCompletionService via DI's
        // IEnumerable<T> support (one per named HttpClient registration
        // above) and builds the code -> connector map. Adding a new
        // dialect (Anthropic, Gemini, Groq, etc.) is a new HttpClient
        // registration + one factory map entry — nothing else.
        // The factory eagerly indexes all typed connector instances, several of which
        // are scoped because they depend on request-scoped OAuth/repository services.
        // Keep the factory scoped so it never captures those connectors in a singleton.
        services.AddScoped<IProviderConnectorFactory, ProviderConnectorFactory>();

        // ── Provider Catalog (PERF-ARKANA-001) ──────────────────────
        // Singleton: one in-memory snapshot of the providers table,
        // shared by every IChatCompletionService. Refreshed on TTL
        // (default 30s) or on explicit Invalidate() from admin write paths.
        services.AddSingleton<IProviderCatalog, ProviderCatalog>();

        // ── OAuth provider integration (FR-3 / P1-3) ────────────
        // Gateway-managed OAuth: device-code / auth-code flows, sealed token
        // storage, and background auto-refresh. Tokens tenant-scoped; all
        // outbound calls still honor the SSRF guard via the provider BaseUrl.
        services.AddHttpClient("oauth", client =>
        {
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IOAuthProviderConfigRepository, OAuthProviderConfigRepository>();
        services.AddScoped<IProviderOAuthTokenRepository, ProviderOAuthTokenRepository>();
        services.AddScoped<IOAuthPendingFlowRepository, OAuthPendingFlowRepository>();
        services.AddScoped<IOAuthFlowService, OAuthFlowService>();
        services.AddScoped<IOAuthTokenResolver, OAuthTokenResolver>();
        services.AddHostedService<OAuthTokenRefreshService>();

        // ── ChatGPT / Codex subscription multi-account pool ──────
        // One connector serves every chatgpt-accN provider; the account pool
        // selects a healthy, non-throttled account at request time. Scoped
        // because it resolves the (scoped) OAuth flow service per request.
        services.AddScoped<ChatGptAccountPool>();
        services.AddScoped<Arkana.Infrastructure.Services.ChatGptAccountService>();

        // ── Gemini (CLIProxyAPI) subscription multi-account pool ──
        // Each gemini-accN is its own AiProvider row pointing at a per-account
        // cliproxy container; the connector targets it via PreferredProviderCode.
        services.AddScoped<Arkana.Infrastructure.Services.GeminiAccountService>();
        services.AddHttpClient<ChatGptCodexChatService>("chatgpt-codex", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddHttpMessageHandler<SsrfSafeHttpHandler>();
        // Usage-window capture for the profile page: the typed connector gets
        // the (scoped) snapshot repository so every upstream response's
        // rate-limit report is persisted best-effort.
        services.AddScoped<IChatCompletionService>(sp =>
        {
            var connector = sp.GetRequiredService<ChatGptCodexChatService>();
            return connector.WithUsageRepository(
                sp.GetRequiredService<IAccountUsageSnapshotRepository>(),
                sp.GetRequiredService<IServiceScopeFactory>());
        });

        // SECURITY: legacy plaintext-credential migration runs first, then the
        // env-var bootstrap. Both are idempotent and only act on rows that need
        // attention. On a freshly-deployed gateway, both no-op.
        services.AddHostedService<LegacyCredentialMigrationService>();
        services.AddHostedService<ApiKeyBootstrapService>();

        // n8n Workflow Integration — configured via N8n section in appsettings.json
        services.AddOptions<N8nOptions>()
            .BindConfiguration(N8nOptions.Section);
        // Singleton: one HttpClient + one n8n session cookie for the lifetime of the gateway.
        // Using AddHttpClient was transient — each injection created a new login session,
        // quickly hitting n8n's brute-force rate limit on /rest/login.
        services.AddSingleton<IN8nService, N8nService>();

        // ── Response cache (PERF-ARKANA-002) ──────────────────────────────
        // Singleton: in-memory cache lives for the lifetime of the process.
        // The SendChatHandler does the read-through and write-back;
        // an admin write to a provider (key rotation, base-url change)
        // calls InvalidateAll().
        services.AddSingleton(_ => InMemoryResponseCacheOptions.Default);
        services.AddSingleton<InMemoryResponseCache>();
        services.AddSingleton<IResponseCache>(sp => sp.GetRequiredService<InMemoryResponseCache>());

        // ── Semantic cache (AI-ARKANA-005) ───────────────────────────
        // Qdrant-backed vector-similarity cache. Wired as a singleton
        // so the gRPC connection pool and collection init are shared.
        // Disabled by default; enable via SemanticCache:Enabled.
        services.AddOptions<SemanticCacheOptions>()
            .BindConfiguration(SemanticCacheOptions.Section);
        services.AddSingleton<ISemanticCache, QdrantSemanticCache>();

        // Cache metrics — single Meter + ObservableGauge for size, plus
        // a 10s background flush that turns the cache's monotonic
        // counters into OTel deltas.
        services.AddSingleton<CacheMetricsExporter>();
        services.AddHostedService<CacheMetricsFlushService>();

        // ── Chat metrics (OBS-ARKANA-002, task #20) ────────────────
        // Singleton: exposes Histogram (latency), Counters (requests,
        // tokens), and ObservableGauges (cache hit ratios). No flush
        // service needed — Histograms and Counters are recorded inline
        // per request; ObservableGauges read cache counters lazily.
        services.AddSingleton<ChatMetricsExporter>();
        services.AddSingleton<IChatMetricsRecorder>(sp => sp.GetRequiredService<ChatMetricsExporter>());

        // ── Enterprise: Multi-tenancy, Plans, Budget (Phase 4) ─
        // Budget enforcement — singleton with DB factory scoping.
        //
        // The `enabled` flag MUST be wired explicitly. BudgetEnforcer's
        // constructor declares `bool enabled = false`, and the DI container
        // cannot resolve a primitive, so the previous
        // `AddSingleton<IBudgetEnforcer, BudgetEnforcer>()` silently bound the
        // default — budget enforcement was permanently OFF and the documented
        // "toggle via BudgetEnforcer:Enabled" had no reader anywhere in the
        // codebase (verified 2026-08-06). Every budget check in SendChatHandler
        // is gated on IsEnabled, so the whole feature was dead code in prod.
        services.AddSingleton<IBudgetEnforcer>(sp => new BudgetEnforcer(
            sp.GetRequiredService<IDbContextFactory<GatewayDbContext>>(),
            sp.GetRequiredService<ILogger<BudgetEnforcer>>(),
            enabled: sp.GetRequiredService<IConfiguration>()
                       .GetValue<bool>("BudgetEnforcer:Enabled")));

        // Rolls elapsed budget periods over. Without this, a tenant that hits
        // its cap stays at 402 permanently — see BudgetResetService remarks.
        services.AddHostedService<BudgetResetService>();

        // ── Batched metering (PERF-ARKANA-005) ─────────────────────────
        // A bounded Channel<T> queue + a BackgroundService that drains
        // it on a fixed cadence. The chat hot path enqueues and
        // returns; the flusher bulk-inserts. This decouples the
        // user-visible chat latency from the DB write latency.
        services.AddOptions<MeteringQueueOptions>()
            .BindConfiguration(MeteringQueueOptions.Section);
        services.AddSingleton<BatchedMeteringQueue>();
        services.AddSingleton<IMeteringQueue>(sp => sp.GetRequiredService<BatchedMeteringQueue>());
        services.AddSingleton<MeteringDbWriter>();
        services.AddHostedService<BatchedMeteringFlushService>();

        // Metering metrics — 10s flush cadence, same as the cache
        // exporter so dashboard panels stay in lock-step.
        services.AddSingleton<MeteringMetricsExporter>();
        services.AddHostedService<MeteringMetricsFlushService>();

        // ── Rate limiting (SEC-ARKANA-005) ──────────────────────────
        // Config sourced from DB via IRateLimitConfigProvider (30s cache).
        // Singleton: per-key bucket state is in-memory and the
        // cleanup timer lives for the process lifetime. Swap for
        // a Redis-backed limiter in Phase 3 (multi-instance).
        services.AddMemoryCache();
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<IRateLimiter>(sp => sp.GetRequiredService<RateLimiter>());

        // ── Input/Output compression (AI-ARKANA-002/003) ────────────
        // Both feature-flagged in CompressionOptions.InputEnabled /
        // OutputEnabled — default off so the rollout is reversible.
        // Singletons: pure functions, no per-request state.
        services.AddOptions<CompressionOptions>()
            .BindConfiguration(CompressionOptions.Section);
        services.AddSingleton<SlimmerInputCompressor>();
        services.AddSingleton<IInputCompressor>(sp => sp.GetRequiredService<SlimmerInputCompressor>());
        services.AddSingleton<TerseOutputCompressor>();
        services.AddSingleton<IOutputCompressor>(sp => sp.GetRequiredService<TerseOutputCompressor>());

        // ══════════════════════════════════════════════════════════════
        //  PHASE 6 — MULTI-AGENT ORCHESTRATION
        // ══════════════════════════════════════════════════════════════

        // Agent system — repository lives here (Infrastructure),
        // orchestrator lives in Gateway.Api (registered in Program.cs).
        services.AddScoped<IAgentRepository, AgentRepository>();

        return services;
    }

    /// <summary>
    /// Binds an options object from configuration without letting a malformed value kill the host.
    /// The .NET binder throws <see cref="InvalidOperationException"/> for values it cannot convert
    /// (an empty GUID from an unset compose variable is the common case), and that exception
    /// escapes on the first read of the options — which for a startup validation means the whole
    /// application fails to start. Config problems belong in a warning plus a disabled feature,
    /// not in a crash loop.
    /// </summary>
    private static void BindTolerantly<TOptions>(IConfigurationSection section, TOptions options)
        where TOptions : class
    {
        try
        {
            section.Bind(options);
        }
        catch (InvalidOperationException)
        {
            // Leave the options at their defaults; the startup check reports the offending key.
        }
    }
}
