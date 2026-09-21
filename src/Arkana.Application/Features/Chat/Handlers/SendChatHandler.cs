using Arkana.Application.Features.Chat.Commands;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arkana.Application.Features.Chat.Handlers;

/// <summary>
/// Handles the SendChatCommand by routing the request to the appropriate AI provider
/// via the model router, tracking token usage, and logging the full request/response.
/// Now supports forwarding tool definitions and tool_choice, and (PERF-ARKANA-002)
/// uses a content-derived response cache to short-circuit repeated identical requests.
/// </summary>
internal sealed class SendChatHandler : IRequestHandler<SendChatCommand, SendChatResult>
{
    /// <summary>
    /// Default cache TTL for chat completions. Five minutes is a
    /// pragmatic default for the common "agent re-sends the same
    /// prompt after every tool call" pattern — long enough to absorb
    /// rapid repetition, short enough that a model upgrade or a
    /// provider re-prompt isn't masked for hours.
    /// </summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);

    private readonly IModelRouter _router;
    private readonly ITokenTracker _tokenTracker;
    private readonly IRequestLogger _requestLogger;
    private readonly IProviderCatalog _providerCatalog;
    private readonly IApiKeyRepository _apiKeyRepo;
    private readonly IModelRepository _modelRepo;
    private readonly FallbackChainExecutor _fallback;
    private readonly IResponseCache _responseCache;
    private readonly ISemanticCache _semanticCache;
    private readonly IChatMetricsRecorder _metrics;
    private readonly IRateLimiter _rateLimiter;
    private readonly IInputCompressor _inputCompressor;
    private readonly IOutputCompressor _outputCompressor;
    private readonly IBudgetEnforcer _budget;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<SendChatHandler> _logger;
    private readonly ResponseCacheOptions _cacheOptions;
    private readonly SemanticCacheOptions _semanticCacheOptions;
    private readonly IProviderTargetPlanner? _targetPlanner;

    public SendChatHandler(IModelRouter router, ITokenTracker tokenTracker,
        IRequestLogger requestLogger,
        IProviderCatalog providerCatalog, IApiKeyRepository apiKeyRepo,
        IModelRepository modelRepo, FallbackChainExecutor fallback,
        IResponseCache responseCache,
        ISemanticCache semanticCache,
        IChatMetricsRecorder metrics,
        IRateLimiter rateLimiter,
        IInputCompressor inputCompressor,
        IOutputCompressor outputCompressor,
        IBudgetEnforcer budget,
        ITenantProvider tenantProvider,
        IOptions<ResponseCacheOptions> cacheOptions,
        IOptions<SemanticCacheOptions> semanticCacheOptions,
        ILogger<SendChatHandler> logger,
        IProviderTargetPlanner? targetPlanner = null)
    {
        _router = router;
        _tokenTracker = tokenTracker;
        _requestLogger = requestLogger;
        _providerCatalog = providerCatalog;
        _apiKeyRepo = apiKeyRepo;
        _modelRepo = modelRepo;
        _fallback = fallback;
        _responseCache = responseCache;
        _semanticCache = semanticCache;
        _metrics = metrics;
        _rateLimiter = rateLimiter;
        _inputCompressor = inputCompressor;
        _outputCompressor = outputCompressor;
        _budget = budget;
        _tenantProvider = tenantProvider;
        _cacheOptions = cacheOptions.Value;
        _semanticCacheOptions = semanticCacheOptions.Value;
        _targetPlanner = targetPlanner;
        _logger = logger;
    }

    public async Task<SendChatResult> Handle(SendChatCommand request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tenantId = _tenantProvider.TenantId
            ?? throw new InvalidOperationException("Authenticated tenant is required.");

        // ── Look up model from DB (for pricing + permission) ─
        var allModels = await _modelRepo.GetAllAsync(tenantId, ct);
        var modelConfig = allModels.FirstOrDefault(m =>
            m.Code.Equals(request.Model, StringComparison.OrdinalIgnoreCase)
            && m.IsEnabled
            && (m.Provider is null || m.Provider.IsEnabled)
            && (string.IsNullOrWhiteSpace(request.PreferredProvider)
                || m.Provider is null
                || m.Provider.Code.Equals(request.PreferredProvider, StringComparison.OrdinalIgnoreCase)
                || m.Provider.Name.Equals(request.PreferredProvider, StringComparison.OrdinalIgnoreCase)));
        // ── API key model permission check ─────────────────
        string? apiKeyName = null;
        Guid? requestedProviderAccountId = null;
        if (!string.IsNullOrEmpty(request.ApiKey))
        {
            var hash = ApiKeyHasher.Hash(request.ApiKey);
            var apiKey = await _apiKeyRepo.GetByKeyHashAsync(hash, ct);

            if (apiKey is null)
            {
                return new SendChatResult
                {
                    Content = "Error: Invalid API key",
                    Provider = "", Model = request.Model,
                    DurationMs = 0,
                    IsError = true, ErrorType = "auth_error", StatusCode = 401
                };
            }

            apiKeyName = apiKey.Name;
            requestedProviderAccountId = apiKey.PreferredProviderAccountId;
            if (!apiKey.IsActive)
            {
                return new SendChatResult
                {
                    Content = "Error: API key is deactivated",
                    Provider = "", Model = request.Model,
                    DurationMs = 0,
                    IsError = true, ErrorType = "auth_error", StatusCode = 401
                };
            }

            // If key has specific model restrictions, select the model row that
            // belongs to this key. The catalog may contain the same public model
            // code under several providers; choosing the first row can reject a
            // valid key before routing (especially for OAuth account pools).
            if (apiKey.AllowedModels.Count > 0)
            {
                var allowedModel = allModels.FirstOrDefault(m =>
                    m.Code.Equals(request.Model, StringComparison.OrdinalIgnoreCase)
                    && apiKey.CanAccessModel(m.Id)
                    && (string.IsNullOrWhiteSpace(request.PreferredProvider)
                        || m.Provider is null
                        || m.Provider.Code.Equals(request.PreferredProvider, StringComparison.OrdinalIgnoreCase)
                        || m.Provider.Name.Equals(request.PreferredProvider, StringComparison.OrdinalIgnoreCase)));
                if (allowedModel is null)
                {
                    _logger.LogWarning(
                        "API key '{KeyName}' tried to access model '{Model}' which is not allowed",
                        apiKey.Name, request.Model);

                    return new SendChatResult
                    {
                        Content = $"Error: API key does not have access to model '{request.Model}'",
                        Provider = "", Model = request.Model,
                        DurationMs = 0,
                        IsError = true, ErrorType = "permission_error", StatusCode = 403
                    };
                }

                modelConfig = allowedModel;
            }
        }

        // ── Route and execute (with fallback chain) ─────────
        // Build a fallback chain: all enabled providers in priority order,
        // with the router's resolved provider (or the explicitly-preferred
        // one) as the primary. If the primary fails with a retryable
        // error, the next provider in the chain is tried.
        var allProviders = await _providerCatalog.GetAllAsync(tenantId, ct);
        AiProvider? primary = null;
        if (!string.IsNullOrEmpty(request.PreferredProvider))
        {
            primary = allProviders.FirstOrDefault(p =>
                p.Code.Equals(request.PreferredProvider, StringComparison.OrdinalIgnoreCase) ||
                p.Name.Equals(request.PreferredProvider, StringComparison.OrdinalIgnoreCase));
        }
        // Route to the provider that actually OWNS the requested model.
        //
        // Fixed 2026-08-06: `primary` was hardcoded to the "opencode"
        // provider for every request, ignoring modelConfig.ProviderId
        // entirely. Any model belonging to another provider (Ollama,
        // Anthropic, …) was still sent to OpenCode, which rejected it —
        // so adding an independent fallback upstream had no effect and a
        // request for an Ollama model returned an OpenCode 401. The model's
        // own provider must win; the hardcoded opencode/priority lookups
        // remain only as a last resort when the model is unknown.
        if (primary is null && modelConfig is not null)
        {
            primary = allProviders.FirstOrDefault(p => p.Id == modelConfig.ProviderId);
        }
        var explicitGeminiRequest = IsGeminiProviderCode(request.PreferredProvider)
            || IsGeminiProviderCode(modelConfig?.Provider?.Code);
        var unknownGeminiModel = modelConfig is null && LooksLikeGeminiModel(request.Model);
        var geminiOwnedRequest = explicitGeminiRequest;
        var stableGeminiAlias = string.Equals(
            request.PreferredProvider, "gemini-subscription", StringComparison.OrdinalIgnoreCase);
        if (unknownGeminiModel && !explicitGeminiRequest)
        {
            return new SendChatResult
            {
                Content = "Error: Gemini provider routing is unavailable.",
                Provider = "gemini",
                Model = request.Model,
                DurationMs = sw.ElapsedMilliseconds,
                IsError = true,
                ErrorType = "routing_error",
                StatusCode = 503,
            };
        }

        ProviderTarget? geminiTarget = null;
        if (explicitGeminiRequest && !stableGeminiAlias)
        {
            if (primary is null || _targetPlanner is null)
            {
                return new SendChatResult
                {
                    Content = "Error: Gemini provider routing is unavailable.",
                    Provider = "gemini",
                    Model = request.Model,
                    DurationMs = sw.ElapsedMilliseconds,
                    IsError = true,
                    ErrorType = "routing_error",
                    StatusCode = 503,
                };
            }

            geminiTarget = await _targetPlanner.ResolveAsync(
                tenantId, primary, request.Model, requestedProviderAccountId, ct);
            if (!geminiTarget.IsResolved)
            {
                return new SendChatResult
                {
                    Content = "Error: Gemini provider routing is unavailable.",
                    Provider = "gemini",
                    Model = request.Model,
                    DurationMs = sw.ElapsedMilliseconds,
                    IsError = true,
                    ErrorType = "routing_error",
                    StatusCode = 503,
                };
            }
        }
        if (geminiOwnedRequest
            && ((!stableGeminiAlias && primary is null)
                || (primary is not null && !IsGeminiProviderCode(primary.Code))))
        {
            return new SendChatResult
            {
                Content = "Error: Gemini provider routing is unavailable.",
                Provider = "gemini",
                Model = request.Model,
                DurationMs = sw.ElapsedMilliseconds,
                IsError = true,
                ErrorType = "routing_error",
                StatusCode = 503,
            };
        }

        primary ??= allProviders.FirstOrDefault(p =>
            p.Code.Equals("opencode", StringComparison.OrdinalIgnoreCase))
            ?? allProviders.OrderBy(p => p.Priority).FirstOrDefault();

        // Use model-level pricing (fallback to provider pricing if model not found)
        var costPerInputToken = modelConfig?.CostPerInputToken ?? 0m;
        var costPerOutputToken = modelConfig?.CostPerOutputToken ?? 0m;

        // Convert DTO messages to domain messages
        var messages = request.Messages
            .Select(m => new ChatMessage
            {
                Role = m.Role,
                Content = m.Content,
                ToolCallId = m.ToolCallId,
                ToolCalls = m.ToolCalls?.Select(tc => new ToolCall
                {
                    Id = tc.Id,
                    Type = tc.Type,
                    Function = new ToolCallFunction
                    {
                        Name = tc.Function.Name,
                        Arguments = tc.Function.Arguments
                    }
                }).ToList()
            })
            .ToList();

        // Convert tool definitions
        List<ToolDefinition>? tools = null;
        if (request.Tools is { Count: > 0 })
        {
            tools = request.Tools.Select(t => new ToolDefinition
            {
                Type = t.Type,
                Function = new ToolFunction
                {
                    Name = t.Function.Name,
                    Description = t.Function.Description,
                    Parameters = t.Function.Parameters,
                    Strict = t.Function.Strict
                }
            }).ToList();
        }

        var chatRequest = new ChatRequest
        {
            Model = request.Model,
            Messages = messages,
            Tools = tools,
            ToolChoice = request.ToolChoice,
            TenantId = tenantId,
        };

        // AI-ARKANA-002: Slimmer input compression. Runs before the
        // response cache lookup so the cache key reflects the
        // compressed shape. The compressor is a pure function
        // (same input → same output) so cache key stability is
        // preserved: the same uncompressed request will always
        // hash to the same compressed cache key.
        chatRequest = _inputCompressor.Compress(chatRequest);

        // AI-ARKANA-006: pin the resolved provider onto the chat request so the
        // connector can target the correct multi-account upstream. For Gemini
        // (gemini-accN) each account is its own AiProvider with its own BaseUrl;
        // the connector uses PreferredProviderCode to reach the exact account the
        // API key is bound to. ChatGPT already does this via its account pool,
        // but passing the code is harmless and keeps the two paths symmetric.
        if (stableGeminiAlias || !string.IsNullOrEmpty(primary?.Code))
        {
            chatRequest = chatRequest with
            {
                PreferredProviderCode = geminiTarget?.RouteKind == ProviderRouteKind.BrokerManagedGemini
                    ? geminiTarget.AccountCode
                    : request.PreferredProvider ?? primary?.Code,
                PreferredProviderId = geminiTarget?.ProviderId ?? primary?.Id,
                PreferredProviderAccountId = geminiTarget?.ProviderAccountId
                    ?? requestedProviderAccountId,
                PreferredProviderAccountCode = geminiTarget?.AccountCode,
                AllowProviderFallback = request.AllowProviderFallback,
                AccountRoutingMode = request.AllowProviderFallback
                    ? AccountRoutingMode.PinWithSameProviderFailover
                    : AccountRoutingMode.StrictPin
            };
        }

        // ── Response cache (PERF-ARKANA-002) — read-through ─────────
        // Caching is skipped when:
        //   - The feature flag is off (default: off in Phase 2 to keep
        //     the rollout reversible).
        //   - The caller explicitly chose a PreferredProvider — those
        //     requests are deliberately picking a specific upstream and
        //     should not collide with the default-provider cache.
        //   - The TTL is non-positive (effectively "do not cache new
        //     entries"; previously-cached entries still serve).
        //   - The request has tool calls defined — tool-using responses
        //     are highly dependent on tool-result ordering in the
        //     message stream, which is fragile to cache. We do not
        //     attempt to canonicalize that surface yet.
        //   - The request has zero messages (degenerate input).
        var cacheable = _cacheOptions.Enabled
            && _cacheOptions.DefaultTtl > TimeSpan.Zero
            && string.IsNullOrEmpty(request.PreferredProvider)
            && (request.Tools is null || request.Tools.Count == 0)
            && chatRequest.Messages.Count > 0;

        CacheKey cacheKey = default;
        if (cacheable)
        {
            cacheKey = CacheKey.From(chatRequest);
            var hit = await _responseCache.GetAsync(cacheKey, ct);

            if (hit is not null)
            {
                sw.Stop();
                _logger.LogInformation(
                    "Chat cache HIT for model {Model} (key={CacheKey}, served_by={Provider})",
                    hit.Model, cacheKey, hit.ServedByProviderName);

                // Account for the cache hit in the dashboard. Cost is
                // the model-priced equivalent of the cached token
                // counts, but we DO NOT bill the upstream — the entry
                // record keeps the audit trail accurate without
                // double-counting in upstream dashboards.
                var hitCost = (hit.InputTokens * costPerInputToken) + (hit.OutputTokens * costPerOutputToken);

                var hitUsage = new TokenUsage(
                    hit.ServedByProviderName,
                    hit.Model,
                    hit.InputTokens,
                    hit.OutputTokens,
                    hitCost,
                    sw.Elapsed,
                    apiKeyName);
                await _tokenTracker.RecordUsageAsync(hitUsage, ct);
                // SEC-ARKANA-005: post-flight TPM charge. Cache hits
                // don't consume upstream tokens, but they still
                // count against the per-key TPM cap (we bill for
                // the cached content as if the upstream had
                // served it).
                if (apiKeyName is not null)
                {
                    _rateLimiter.RecordTokenUsageAsync(apiKeyName, hit.InputTokens, hit.OutputTokens);
                }

                var hitLog = new RequestLog
                {
                    Provider = hit.ServedByProviderName,
                    Model = hit.Model,
                    ApiKeyName = apiKeyName,
                    TenantId = _tenantProvider.TenantId ?? Guid.Empty,
                    RequestedProviderCode = request.PreferredProvider,
                    RequestedProviderAccountId = requestedProviderAccountId,
                    Messages = messages,
                    ResponseContent = hit.Content,
                    ToolCalls = hit.ToolCalls,
                    InputTokens = hit.InputTokens,
                    OutputTokens = hit.OutputTokens,
                    Cost = hitCost,
                    Duration = sw.Elapsed,
                    Timestamp = DateTimeOffset.UtcNow,
                    IsError = false
                };
                await _requestLogger.RecordAsync(hitLog, ct);

                // AI-ARKANA-003: terse output compression on the
                // cache-hit path. Note we compress a fresh local
                // (hitContent) so the cached entry itself is left
                // alone — re-compressing an already-compressed
                // string on subsequent hits would be a no-op but
                // still cost a regex pass.
                var hitContent = _outputCompressor.Compress(hit.Content);

                _metrics.RecordLatency(sw.ElapsedMilliseconds, hit.ServedByProviderName, hit.Model, false);
                _metrics.RecordRequest(hit.ServedByProviderName, hit.Model, false);
                _metrics.RecordTokenUsage(hit.ServedByProviderName, hit.Model, hit.InputTokens, hit.OutputTokens);

                return new SendChatResult
                {
                    Content = hitContent,
                    Provider = hit.ServedByProviderName,
                    Model = hit.Model,
                    InputTokens = hit.InputTokens,
                    OutputTokens = hit.OutputTokens,
                    EstimatedCost = hitCost,
                    DurationMs = sw.ElapsedMilliseconds,
                    ToolCalls = hit.ToolCalls
                };
            }
            _logger.LogDebug("Chat cache MISS for model {Model} (key={CacheKey})", request.Model, cacheKey);
        }

        // ── Semantic cache (AI-ARKANA-005) — lookup after exact miss ─
        // If exact-match missed, try the semantic cache. It may return
        // a response for a semantically similar (but not identical)
        // prompt. The same caching rules apply — we skip when the
        // request has tools, preferred provider, etc.
        CachedResponse? semanticHit = null;
        if (cacheable && _semanticCacheOptions.Enabled)
        {
            semanticHit = await _semanticCache.GetSimilarAsync(chatRequest, ct);

            if (semanticHit is not null)
            {
                sw.Stop();
                _logger.LogInformation(
                    "Semantic cache HIT for model {Model} (served_by={Provider})",
                    semanticHit.Model, semanticHit.ServedByProviderName);

                var hitCost = (semanticHit.InputTokens * costPerInputToken) + (semanticHit.OutputTokens * costPerOutputToken);

                var hitUsage = new TokenUsage(
                    semanticHit.ServedByProviderName,
                    semanticHit.Model,
                    semanticHit.InputTokens,
                    semanticHit.OutputTokens,
                    hitCost,
                    sw.Elapsed,
                    apiKeyName);
                await _tokenTracker.RecordUsageAsync(hitUsage, ct);
                if (apiKeyName is not null)
                {
                    _rateLimiter.RecordTokenUsageAsync(apiKeyName, semanticHit.InputTokens, semanticHit.OutputTokens);
                }

                var hitLog = new RequestLog
                {
                    Provider = semanticHit.ServedByProviderName,
                    Model = semanticHit.Model,
                    ApiKeyName = apiKeyName,
                    TenantId = _tenantProvider.TenantId ?? Guid.Empty,
                    RequestedProviderCode = request.PreferredProvider,
                    RequestedProviderAccountId = requestedProviderAccountId,
                    Messages = messages,
                    ResponseContent = semanticHit.Content,
                    ToolCalls = semanticHit.ToolCalls,
                    InputTokens = semanticHit.InputTokens,
                    OutputTokens = semanticHit.OutputTokens,
                    Cost = hitCost,
                    Duration = sw.Elapsed,
                    Timestamp = DateTimeOffset.UtcNow,
                    IsError = false
                };
                await _requestLogger.RecordAsync(hitLog, ct);

                var hitContent = _outputCompressor.Compress(semanticHit.Content);

                _metrics.RecordLatency(sw.ElapsedMilliseconds, semanticHit.ServedByProviderName, semanticHit.Model, false);
                _metrics.RecordRequest(semanticHit.ServedByProviderName, semanticHit.Model, false);
                _metrics.RecordTokenUsage(semanticHit.ServedByProviderName, semanticHit.Model, semanticHit.InputTokens, semanticHit.OutputTokens);

                return new SendChatResult
                {
                    Content = hitContent,
                    Provider = semanticHit.ServedByProviderName,
                    Model = semanticHit.Model,
                    InputTokens = semanticHit.InputTokens,
                    OutputTokens = semanticHit.OutputTokens,
                    EstimatedCost = hitCost,
                    DurationMs = sw.ElapsedMilliseconds,
                    ToolCalls = semanticHit.ToolCalls
                };
            }
        }

        // ── Budget enforcement (ENT-ARKANA-003) — reservation ────
        // Before hitting the upstream, reserve estimated tokens from
        // the tenant's budget. If the budget is exhausted, return 429.
        // The reservation is returned (Release) after the actual usage
        // is known — even on failure — so over-reservation is released.
        long reservedInput = 0, reservedOutput = 0;
        if (_budget.IsEnabled)
        {
            // Estimate: input ~chars/4, output ~max_tokens or 4096 default
            var msgText = string.Concat(chatRequest.Messages.Select(m => m.Content ?? ""));
            reservedInput = Math.Max(1, msgText.Length / 4);
            reservedOutput = 4096; // default max_tokens estimate

            if (!await _budget.TryReserveAsync(tenantId, reservedInput, reservedOutput, ct))
            {
                _logger.LogWarning(
                    "Budget exceeded for tenant {TenantId}: {Model}",
                    tenantId, request.Model);

                _metrics.RecordRequest("budget", request.Model, true);

                return new SendChatResult
                {
                    Content = "Error: Monthly budget exceeded. Please upgrade your plan or wait for the next billing period.",
                    Provider = "budget",
                    Model = request.Model,
                    DurationMs = sw.ElapsedMilliseconds,
                    IsError = true, ErrorType = "budget_exceeded", StatusCode = 402,
                };
            }
        }

        // ── Build the fallback chain (only on cache miss) ─────
        // Deferred past the cache check so a cached response bypasses
        // the routing machinery entirely. Cache hits never need to
        // know about providers; the cached entry already records
        // which one served it.
        var allServices = await _router.GetAllProvidersAsync(ct);
        // An unpinned legacy request has no ownership boundary and retains the
        // catalog fallback policy. Once an API key supplies a provider pin,
        // fallback is opt-in and is carried explicitly by the key policy.
        var allowProviderFallback = !geminiOwnedRequest
            && (request.AllowProviderFallback
                || string.IsNullOrWhiteSpace(request.PreferredProvider));
        IReadOnlyList<IChatCompletionService> chain;
        if (stableGeminiAlias || geminiTarget?.RouteKind == ProviderRouteKind.BrokerManagedGemini)
        {
            chain = allServices
                .Where(s => s.ProviderName.Equals("gemini-subscription", StringComparison.OrdinalIgnoreCase))
                .Take(1)
                .ToArray();
        }
        else if (geminiTarget?.RouteKind == ProviderRouteKind.NativeGemini)
        {
            chain = allServices
                .Where(s => s.ProviderName.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
                .Take(1)
                .ToArray();
        }
        else
        {
            chain = FallbackChainExecutor.BuildChain(allProviders, primary, allServices, _logger,
                allowProviderFallback);
        }

        if (chain.Count == 0)
        {
            return new SendChatResult
            {
                Content = "Error: No providers available",
                Provider = "", Model = request.Model,
                DurationMs = 0,
                IsError = true, ErrorType = "no_providers", StatusCode = 503
            };
        }

        // Execute through the fallback chain. The first provider in `chain`
        // is the primary; the executor walks subsequent ones on retryable
        // failure. The returned FallbackExecution tells us which provider
        // actually served the request and how many attempts it took.
        var execution = await _fallback.ExecuteAsync(chain, chatRequest, ct);
        var result = execution.Result;
        var providerName = execution.ServedBy.ProviderName;
        // Durable usage rows historically had only the connector name. For
        // broker-managed accounts that collapses every subscription account
        // into the shared alias and makes cost/metering attribution wrong.
        // Keep the public connector name for request logs/metrics, but charge
        // the resolved account code in the usage record below.
        var usageProviderName = result.ResolvedProviderAccountCode ?? providerName;

        sw.Stop();

        // ── Budget enforcement — release over-reservation ──
        // Return unused tokens back to the budget. Runs even on
        // provider failure so the reservation isn't permanently lost.
        if (_budget.IsEnabled && (reservedInput > 0 || reservedOutput > 0))
        {
            await _budget.ReleaseAsync(tenantId,
                reservedInput, reservedOutput,
                result.InputTokens, result.OutputTokens, ct);
        }

        // Calculate cost using model-specific pricing
        var cost = (result.InputTokens * costPerInputToken) + (result.OutputTokens * costPerOutputToken);

        var usage = new TokenUsage(
            usageProviderName,
            result.Model,
            result.InputTokens,
            result.OutputTokens,
            cost,
            sw.Elapsed,
            apiKeyName);

        await _tokenTracker.RecordUsageAsync(usage, ct);

        // SEC-ARKANA-005: post-flight TPM charge against the
        // per-key bucket. Even on a failure we charge the input
        // tokens (the upstream did the work) — this matches the
        // billing posture the metering uses.
        if (apiKeyName is not null && (result.InputTokens > 0 || result.OutputTokens > 0))
        {
            _rateLimiter.RecordTokenUsageAsync(apiKeyName, result.InputTokens, result.OutputTokens);
        }

        // ── Full request/response logging ─────────────────
        var requestLog = new RequestLog
        {
            Provider = providerName,
            Model = result.Model,
            ApiKeyName = apiKeyName,
            TenantId = _tenantProvider.TenantId ?? Guid.Empty,
            RequestedProviderCode = request.PreferredProvider,
            RequestedProviderAccountId = requestedProviderAccountId,
            Messages = messages,
            ResponseContent = result.Content,
            ToolCalls = result.ToolCalls,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            Cost = cost,
            Duration = sw.Elapsed,
            Timestamp = DateTimeOffset.UtcNow,
            IsError = !result.IsSuccess,
            ErrorMessage = result.ErrorMessage,
            ResolvedProviderAccountId = result.ResolvedProviderAccountId,
            ResolvedProviderAccountCode = result.ResolvedProviderAccountCode,
            RouteKind = string.IsNullOrWhiteSpace(result.RouteKind) || result.RouteKind == "unknown" ? "direct" : result.RouteKind,
            ViaMitmAgent = request.ViaMitmAgent
        };
        await _requestLogger.RecordAsync(requestLog, ct);

        if (!result.IsSuccess)
        {
            _logger.LogError("Provider {Provider} failed: {Error}", providerName, result.ErrorMessage);

            _metrics.RecordLatency(sw.ElapsedMilliseconds, providerName, result.Model, true);
            _metrics.RecordRequest(providerName, result.Model, true);
            _metrics.RecordTokenUsage(providerName, result.Model, result.InputTokens, result.OutputTokens);

            return new SendChatResult
            {
                Content = $"Error: {result.ErrorMessage}",
                Provider = providerName,
                Model = result.Model,
                InputTokens = result.InputTokens,
                OutputTokens = result.OutputTokens,
                EstimatedCost = cost,
                DurationMs = sw.ElapsedMilliseconds,
                IsError = true,
                ErrorType = "upstream_error",
                // Propagate the upstream status when we have one (429 stays a
                // 429); otherwise 502 — the gateway itself is fine, the
                // provider behind it is not.
                StatusCode = result.UpstreamStatus ?? 502
            };
        }

        _logger.LogInformation(
            "Chat completed: provider={Provider} model={Model} tokens={Tokens} cost={Cost:F6} duration={Duration}",
            providerName, result.Model, usage.TotalTokens, usage.Cost, sw.Elapsed);

        // ── Response cache (PERF-ARKANA-002) — write-back ──────────
        // Only successful responses go into the cache. Failure
        // responses are not stable enough to cache (a transient 503
        // today might be the same prompt served correctly tomorrow),
        // and caching them would amplify the outage.
        if (cacheable)
        {
            var entry = new CachedResponse(
                Content: result.Content,
                Model: result.Model,
                InputTokens: result.InputTokens,
                OutputTokens: result.OutputTokens,
                ToolCallsCount: result.ToolCalls?.Count ?? 0,
                ToolCalls: result.ToolCalls,
                ServedByProviderName: providerName,
                CachedAt: DateTimeOffset.UtcNow);
            try
            {
                await _responseCache.SetAsync(cacheKey, entry, _cacheOptions.DefaultTtl, ct);
            }
            catch (Exception ex)
            {
                // Cache write failures must NEVER break a successful
                // chat response. The cost of a stale-or-missing entry
                // is just the next request being slow; the cost of
                // failing here is a broken user-visible chat.
                _logger.LogWarning(ex, "Cache write failed for key {CacheKey}", cacheKey);
            }

            // ── Semantic cache (AI-ARKANA-005) — write-back ──
            // Also store in the vector cache if enabled. Uses the
            // same CachedResponse entry built for the exact-match
            // cache. Failures are swallowed — they never break the
            // chat response.
            if (_semanticCacheOptions.Enabled)
            {
                try
                {
                    await _semanticCache.StoreAsync(chatRequest, entry, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Semantic cache write failed for model {Model}", request.Model);
                }
            }
        }

        // AI-ARKANA-003: terse output compression. Done after
        // metering so the InputTokens/OutputTokens we record
        // reflect the model's actual generation, not the
        // compressed-on-the-wire size the client sees.
        var responseContent = _outputCompressor.Compress(result.Content);

        _metrics.RecordLatency(sw.ElapsedMilliseconds, providerName, result.Model, false);
        _metrics.RecordRequest(providerName, result.Model, false);
        _metrics.RecordTokenUsage(providerName, result.Model, result.InputTokens, result.OutputTokens);

        return new SendChatResult
        {
            Content = responseContent,
            Provider = providerName,
            Model = result.Model,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            EstimatedCost = cost,
            DurationMs = sw.ElapsedMilliseconds,
            ToolCalls = result.ToolCalls
        };
    }

    private static bool IsGeminiProviderCode(string? code)
        => code is not null
            && (code.Equals("gemini", StringComparison.OrdinalIgnoreCase)
                || code.Equals("gemini-subscription", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("gemini-acc", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeGeminiModel(string model)
        => model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("google/gemini", StringComparison.OrdinalIgnoreCase);
}
