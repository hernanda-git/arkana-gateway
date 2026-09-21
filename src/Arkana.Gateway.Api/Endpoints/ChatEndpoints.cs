using Arkana.Application.Features.Chat.Commands;
using Arkana.Application.Features.Chat.Queries;
using Arkana.Domain.Interfaces;
using Arkana.Domain.ValueObjects;
using Arkana.Gateway.Api.Services;
using Arkana.Gateway.Api.Authorization;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Arkana.Gateway.Api.Endpoints;

/// <summary>
/// Maps OpenAI-compatible chat completion, token usage, and model listing endpoints.
/// Supports tools/function calling for both streaming and non-streaming modes.
/// Uses Application-layer DTOs for tool definitions and tool calls.
/// </summary>
public static class ChatEndpoints
{
    private static readonly string[] CodexSupportedTools = ["shell_command"];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Maps all chat-related endpoints under the /v1 route group.
    /// </summary>
    public static void MapChatEndpoints(this WebApplication app)
    {
        var chat = app.MapGroup("/v1");

        // OpenAI-compatible chat completion
        chat.MapPost("/chat/completions", async (ChatCompletionRequest request, IMediator mediator,
            HttpContext httpContext, IConfiguration config, IHttpClientFactory httpClientFactory,
            ITokenTracker tokenTracker, IRequestLogger requestLogger,
            ActiveStreamCounter streamCounter,
            Arkana.Domain.Interfaces.IAiProviderRepository providerRepo,
            Arkana.Domain.Services.ICredentialVault vault) =>
        {
            // Re-read the credential the middleware already validated.
            // ApiKeyExtractor is the shared parser — see its remarks for why
            // the old inline `.Replace("Bearer ", "")` was wrong.
            var rawApiKey = Middleware.ApiKeyExtractor.Extract(httpContext.Request);

            var loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("Arkana.Gateway.Api.ChatEndpoints");

            // Additive audit attribution: identifies an employee MITM agent (e.g. "antigravity")
            // when present. Null otherwise, so direct API clients are completely unaffected.
            var viaMitmAgent = httpContext.Request.Headers["X-Via-Mitm-Agent"].FirstOrDefault();

            // ── Streaming mode ─────────────────────────────────
            if (request.Stream == true)
            {
                // ── Resolve the provider for the requested model ─────
                // Look up which provider owns this model, so requests
                // route to the correct upstream (not always opencode.ai).
                // This is critical for agent tool calls: opencode.ai
                // blocks them with 403/1010, but direct providers
                // (DeepSeek, OpenAI) support them.
                string upstreamBaseUrl = "https://opencode.ai/zen/go/v1";
                string? upstreamApiKey = null;

                var modelName = request.Model ?? Arkana.Gateway.Api.Configuration.GatewayDefaults.DefaultModel;
                var preferredProvider = httpContext.Items.TryGetValue("ApiKeyPreferredProvider", out var pp) ? pp?.ToString() : null;
                var allowProviderFallback = httpContext.Items.TryGetValue("ApiKeyAllowProviderFallback", out var af)
                    && af is true;
                var tenantProvider = httpContext.RequestServices.GetService<ITenantProvider>();
                if (tenantProvider?.TenantId is not { } tenantId)
                    return Results.Problem("Authenticated tenant is required.", statusCode: 401);
                Arkana.Domain.Entities.Model? modelConfig = null;
                try
                {
                    var modelRepo = httpContext.RequestServices.GetRequiredService<Arkana.Domain.Interfaces.IModelRepository>();
                    var allModels = await modelRepo.GetAllAsync(tenantId, httpContext.RequestAborted);
                    modelConfig = allModels
                        .Where(m => m.Code.Equals(modelName, StringComparison.OrdinalIgnoreCase)
                            && m.IsEnabled && m.Provider.IsEnabled
                            && ApiKeyModelAuthorization.IsAllowed(httpContext, m)
                            && (preferredProvider is null
                                || m.Provider.Code.Equals(preferredProvider, StringComparison.OrdinalIgnoreCase)
                                || m.Provider.Name.Equals(preferredProvider, StringComparison.OrdinalIgnoreCase)))
                        .OrderByDescending(m => preferredProvider is not null
                            && m.Provider.Code.Equals(preferredProvider, StringComparison.OrdinalIgnoreCase))
                        .ThenBy(m => m.Provider.Code, StringComparer.Ordinal)
                        .ThenBy(m => m.Name, StringComparer.Ordinal)
                        .FirstOrDefault();

                    if (ApiKeyModelAuthorization.IsRestricted(httpContext) && modelConfig is null)
                        return Results.Problem("API key does not have access to the requested model.", statusCode: 403);

                    if (modelConfig?.Provider is not null)
                    {
                        if (!string.IsNullOrEmpty(modelConfig.Provider.BaseUrl))
                            upstreamBaseUrl = modelConfig.Provider.BaseUrl;
                        // SECURITY: decrypt the sealed ApiKey just-in-time for the upstream call.
                        // The plaintext is used immediately and goes out of scope after the request.
                        var decrypted = modelConfig.Provider.DecryptApiKey(vault);
                        if (!string.IsNullOrEmpty(decrypted))
                            upstreamApiKey = decrypted;
                    }

                    if (modelConfig is null
                        && preferredProvider is not null
                        && (!allowProviderFallback
                            || IsGeminiProviderCode(preferredProvider))
                        && !preferredProvider.Equals("gemini-subscription", StringComparison.OrdinalIgnoreCase))
                    {
                        var providers = await providerRepo.GetAllAsync(tenantId, httpContext.RequestAborted);
                        var pinned = providers.FirstOrDefault(p => p.IsEnabled
                            && (p.Code.Equals(preferredProvider, StringComparison.OrdinalIgnoreCase)
                                || p.Name.Equals(preferredProvider, StringComparison.OrdinalIgnoreCase)));
                        if (pinned is null)
                            return Results.Problem($"Pinned provider '{preferredProvider}' is unavailable.", statusCode: 503);
                        if (!string.IsNullOrEmpty(pinned.BaseUrl))
                            upstreamBaseUrl = pinned.BaseUrl;
                        var decrypted = pinned.DecryptApiKey(vault);
                        if (!string.IsNullOrEmpty(decrypted))
                            upstreamApiKey = decrypted;
                    }
                }
                catch when ((allowProviderFallback || preferredProvider is null)
                    && !IsGeminiProviderCode(preferredProvider)
                    && !LooksLikeGeminiModel(modelName))
                {
                    /* explicitly configured fallback may use OpenCode defaults */
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Chat provider routing failed closed.");
                    return Results.Problem("Provider routing is unavailable.", statusCode: 503);
                }

                if (!allowProviderFallback
                    && LooksLikeGeminiModel(modelName)
                    && !IsGeminiProviderCode(modelConfig?.Provider?.Code)
                    && !IsGeminiProviderCode(preferredProvider))
                    return Results.Problem("Gemini model routing could not be resolved safely.", statusCode: 503);

                // ChatGPT / Codex models are served by the ChatGptCodexChatService
                // (which owns the OAuth token + ChatGPT-Account-Id header). It is NOT
                // an OpenAI-compatible HTTP upstream with a BaseUrl, so the generic
                // upstream-proxy path below would misroute to OpenCode. Stream it
                // directly from /codex instead.
                //
                // Model codes are not globally unique (e.g. gpt-5-codex exists on both
                // the ChatGPT account and the OpenAI providers). Resolve unambiguously:
                // route to ChatGPT when the model's provider is chatgpt*, OR when the
                // presenting API key's preferred provider is chatgpt* (the designed
                // per-client routing mechanism).
                var isChatGptModel = (modelConfig?.Provider is not null
                    && modelConfig.Provider.Code.StartsWith("chatgpt", StringComparison.OrdinalIgnoreCase))
                    || (preferredProvider is not null
                    && preferredProvider.StartsWith("chatgpt", StringComparison.OrdinalIgnoreCase));
                if (isChatGptModel)
                {
                    var akName = httpContext.Items.TryGetValue("ApiKeyName", out var akNameObj) ? akNameObj?.ToString() : null;
                    // ChatGptCodexChatService is registered as an IChatCompletionService
                    // (part of the connector collection), not as a directly-resolvable
                    // concrete type — resolve it through the interface.
                    var chatGptSvc = httpContext.RequestServices
                        .GetServices<Arkana.Domain.Interfaces.IChatCompletionService>()
                        .OfType<Arkana.Infrastructure.AI.ChatGptCodexChatService>()
                        .FirstOrDefault();
                    if (chatGptSvc is null)
                        return Results.Problem("ChatGPT connector is not available.", statusCode: 503);
                    return await StreamChatGptAsync(request, chatGptSvc, httpContext, tokenTracker,
                        requestLogger, streamCounter, providerRepo, akName, modelName, preferredProvider,
                        allowProviderFallback, viaMitmAgent);
                }

                var targetProvider = modelConfig?.Provider;
                if (preferredProvider is not null)
                {
                    var providers = await providerRepo.GetAllAsync(tenantId, httpContext.RequestAborted);
                    targetProvider = providers.FirstOrDefault(p =>
                        p.Code.Equals(preferredProvider, StringComparison.OrdinalIgnoreCase)
                        || p.Name.Equals(preferredProvider, StringComparison.OrdinalIgnoreCase))
                        ?? targetProvider;
                }

                var targetPlanner = httpContext.RequestServices.GetService<IProviderTargetPlanner>();
                var requestedAccountId = httpContext.Items.TryGetValue(
                    "ApiKeyPreferredProviderAccountId", out var accountIdObject)
                    && accountIdObject is Guid accountId
                    ? accountId
                    : (Guid?)null;
                ProviderTarget? geminiTarget = null;
                if (preferredProvider?.Equals("gemini-subscription", StringComparison.OrdinalIgnoreCase) != true
                    && targetProvider is not null
                    && IsGeminiProviderCode(targetProvider.Code))
                {
                    if (targetPlanner is null)
                        return Results.Problem("Gemini provider ownership is unavailable.", statusCode: 503);

                    geminiTarget = await targetPlanner.ResolveAsync(
                        tenantId, targetProvider, modelName, requestedAccountId, httpContext.RequestAborted);
                    if (!geminiTarget.IsResolved)
                        return Results.Problem("Gemini provider ownership is invalid.", statusCode: 503);
                }

                var isBrokerManagedGemini = preferredProvider?.Equals(
                    "gemini-subscription", StringComparison.OrdinalIgnoreCase) == true
                    || geminiTarget?.RouteKind == ProviderRouteKind.BrokerManagedGemini;
                if (isBrokerManagedGemini)
                {
                    var broker = httpContext.RequestServices.GetServices<Arkana.Domain.Interfaces.IChatCompletionService>()
                        .FirstOrDefault(s => s.ProviderName.Equals("gemini-subscription", StringComparison.OrdinalIgnoreCase));
                    if (broker is null)
                        return Results.Problem("Gemini subscription connector is not available.", statusCode: 503);

                    if (broker is not Arkana.Domain.Interfaces.IStreamingChatCompletionService streamingBroker)
                        return Results.Problem("Gemini subscription streaming connector is not available.", statusCode: 503);

                    return await StreamGeminiBrokerAsync(request, streamingBroker, httpContext, tokenTracker,
                        requestLogger, streamCounter, providerRepo, modelConfig?.Provider,
                        geminiTarget?.AccountCode ?? preferredProvider,
                        geminiTarget?.ProviderId ?? targetProvider?.Id,
                        geminiTarget?.ProviderAccountId,
                        geminiTarget?.AccountCode,
                        viaMitmAgent);
                }

                var isNativeGemini = geminiTarget?.RouteKind == ProviderRouteKind.NativeGemini;
                if (isNativeGemini)
                {
                    var nativeGemini = httpContext.RequestServices.GetServices<Arkana.Domain.Interfaces.IChatCompletionService>()
                        .FirstOrDefault(s => s.ProviderName.Equals("Gemini", StringComparison.OrdinalIgnoreCase));
                    if (nativeGemini is null)
                        return Results.Problem("Native Gemini connector is not available.", statusCode: 503);
                    if (request.Stream == true)
                    {
                        if (nativeGemini is not Arkana.Domain.Interfaces.IStreamingChatCompletionService nativeStreaming)
                            return Results.Problem("Native Gemini streaming connector is not available.", statusCode: 503);
                        return await StreamGeminiBrokerAsync(request, nativeStreaming, httpContext, tokenTracker,
                                requestLogger, streamCounter, providerRepo, targetProvider,
                                targetProvider?.Code ?? "gemini", geminiTarget?.ProviderId ?? targetProvider?.Id,
                                geminiTarget?.ProviderAccountId, geminiTarget?.AccountCode,
                                viaMitmAgent);
                    }
                    var nativeResult = await nativeGemini.CompleteAsync(BuildChatRequest(
                        request, targetProvider?.Code ?? "gemini",
                        geminiTarget?.ProviderId ?? targetProvider?.Id,
                        geminiTarget?.ProviderAccountId,
                        geminiTarget?.AccountCode, tenantId), httpContext.RequestAborted);
                    if (!nativeResult.IsSuccess)
                        return Results.Problem("Native Gemini routing failed.", statusCode: nativeResult.UpstreamStatus is >= 400 and <= 599 ? nativeResult.UpstreamStatus : 503);
                    var nativeProviderCode = nativeResult.ResolvedProviderAccountCode
                        ?? targetProvider?.Code
                        ?? "gemini";
                    var nativeCost = (nativeResult.InputTokens * (targetProvider?.CostPerInputToken ?? 0m))
                        + (nativeResult.OutputTokens * (targetProvider?.CostPerOutputToken ?? 0m));
                    await tokenTracker.RecordUsageAsync(new TokenUsage(nativeProviderCode, nativeResult.Model, nativeResult.InputTokens, nativeResult.OutputTokens, nativeCost, nativeResult.Duration, httpContext.Items.TryGetValue("ApiKeyName", out var nativeNameObj) ? nativeNameObj?.ToString() : null));
                    await requestLogger.TryRecordWithContextAsync(new RequestLog { Provider = nativeProviderCode, Model = nativeResult.Model, ApiKeyName = httpContext.Items.TryGetValue("ApiKeyName", out var nativeLogNameObj) ? nativeLogNameObj?.ToString() : null, RequestedProviderCode = preferredProvider, ResolvedProviderAccountCode = nativeResult.ResolvedProviderAccountCode, RouteKind = nativeResult.RouteKind, Messages = request.Messages?.Select(m => new ChatMessage { Role = m.Role ?? "user", Content = m.Content ?? "" }).ToList() ?? [], ResponseContent = nativeResult.Content, InputTokens = nativeResult.InputTokens, OutputTokens = nativeResult.OutputTokens, Cost = nativeCost, Duration = nativeResult.Duration, Timestamp = DateTimeOffset.UtcNow }, httpContext);
                    var nativeToolCalls = nativeResult.ToolCalls?.Select(tc => new { id = tc.Id, type = "function", function = new { name = tc.FunctionName, arguments = tc.FunctionArguments } }).ToArray();
                    var nativeFinishReason = nativeToolCalls is { Length: > 0 } ? "tool_calls" : "stop";
                    httpContext.Response.ContentType = "text/event-stream";
                    await httpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { choices = new[] { new { index = 0, message = new { role = "assistant", content = nativeResult.Content, tool_calls = nativeToolCalls }, finish_reason = nativeFinishReason } }, model = nativeResult.Model })}\n\ndata: [DONE]\n\n", httpContext.RequestAborted);
                    return Results.Empty;
                }

                // Native provider code "gemini" is handled explicitly above; all other providers
                if (string.IsNullOrEmpty(upstreamApiKey))
                {
                    try
                    {
                        var providers = await providerRepo.GetAllAsync(tenantId, httpContext.RequestAborted);
                        var openCodeProvider = providers.FirstOrDefault(p =>
                            p.Code.Equals("opencode", StringComparison.OrdinalIgnoreCase));
                        // SECURITY: decrypt the sealed ApiKey just-in-time.
                        if (openCodeProvider is not null)
                        {
                            var decrypted = openCodeProvider.DecryptApiKey(vault);
                            if (!string.IsNullOrEmpty(decrypted))
                                upstreamApiKey = decrypted;
                        }
                    }
                    catch { /* fall through to env var */ }
                }

                if (string.IsNullOrEmpty(upstreamApiKey))
                    upstreamApiKey = Environment.GetEnvironmentVariable("OPENCODE_GO_API_KEY") ?? "";

                httpContext.Response.ContentType = "text/event-stream";
                httpContext.Response.Headers.CacheControl = "no-cache";
                httpContext.Response.Headers.Connection = "keep-alive";
                httpContext.Response.Headers["X-Accel-Buffering"] = "no";
                streamCounter.Increment();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var collectedContent = new System.Text.StringBuilder();
                var requestMessages = request.Messages?.Select(m => new ChatMessage { Role = m.Role ?? "user", Content = m.Content ?? "" }).ToList() ?? [];
                var genericProvider = modelConfig?.Provider;
                var genericProviderCode = genericProvider?.Code ?? preferredProvider ?? "opencode";
                var genericInputRate = genericProvider?.CostPerInputToken ?? 0m;
                var genericOutputRate = genericProvider?.CostPerOutputToken ?? 0m;

                string? apiKeyName = httpContext.Items.TryGetValue("ApiKeyName", out var nameObj) ? nameObj?.ToString() : null;
                int promptTokens = 0, completionTokens = 0;
                string? errorMessage = null;

                try
                {
                    // Pick the streaming client by upstream scheme: the
                    // "opencode-streaming" client carries the SSRF guard,
                    // which blocks plain-HTTP/private hosts and so cannot
                    // reach a self-hosted upstream such as Ollama.
                    var client = httpClientFactory.CreateClient(
                        upstreamBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                            ? "opencode-streaming"
                            : "local-streaming");
                    client.BaseAddress = new Uri(upstreamBaseUrl.TrimEnd('/') + "/");
                    if (!string.IsNullOrEmpty(upstreamApiKey))
                        client.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", upstreamApiKey);

                    // Build streaming request body — now with tools support
                    var bodyDict = new Dictionary<string, object?>
                    {
                        ["model"] = request.Model ?? Arkana.Gateway.Api.Configuration.GatewayDefaults.DefaultModel,
                        ["messages"] = request.Messages?.Select(m => BuildMessageDict(m)),
                        ["stream"] = true
                    };

                    // Forward tool definitions (use Application-level ToolDefinitionDto)
                    if (request.Tools is { Count: > 0 })
                    {
                        bodyDict["tools"] = request.Tools.Select(t => new Dictionary<string, object?>
                        {
                            ["type"] = t.Type,
                            ["function"] = new Dictionary<string, object?>
                            {
                                ["name"] = t.Function.Name,
                                ["description"] = t.Function.Description,
                                ["parameters"] = t.Function.Parameters,
                                ["strict"] = t.Function.Strict
                            }
                        }).ToList();
                    }

                    // Forward tool_choice
                    if (request.ToolChoice is not null)
                    {
                        bodyDict["tool_choice"] = request.ToolChoice;
                    }

                    var upstreamJson = JsonSerializer.Serialize(bodyDict, JsonOpts);

                    var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
                    {
                        Content = new StringContent(upstreamJson, System.Text.Encoding.UTF8, "application/json")
                    };

                    using var upstreamResponse = await client.SendAsync(upstreamRequest,
                        HttpCompletionOption.ResponseHeadersRead, httpContext.RequestAborted);
                    upstreamResponse.EnsureSuccessStatusCode();

                    // Stream SSE response from upstream directly to client
                    using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(httpContext.RequestAborted);
                    using var reader = new StreamReader(upstreamStream);

                    // State machine for stripping <think>...</think> blocks that
                    // can span multiple streaming chunks (MiniMax M3, etc.)
                    var inThinkBlock = false;

                    while (await reader.ReadLineAsync(httpContext.RequestAborted) is { } line)
                    {
                        if (string.IsNullOrEmpty(line)) continue;

                        if (line.StartsWith("data: ", StringComparison.Ordinal))
                        {
                            var data = line[6..];

                            if (data != "[DONE]")
                            {
                                try
                                {
                                    using var doc = JsonDocument.Parse(data);
                                    var usageElem = doc.RootElement.TryGetProperty("usage", out var u) ? (JsonElement?)u : null;
                                    if (usageElem is not null)
                                    {
                                        if (usageElem.Value.TryGetProperty("prompt_tokens", out var pt))
                                            promptTokens = pt.GetInt32();
                                        if (usageElem.Value.TryGetProperty("completion_tokens", out var ct))
                                            completionTokens = ct.GetInt32();
                                    }
                                    var choices = doc.RootElement.TryGetProperty("choices", out var c) ? (JsonElement?)c : null;
                                    if (choices is not null && choices.Value.ValueKind == JsonValueKind.Array && choices.Value.GetArrayLength() > 0)
                                    {
                                        var delta = choices.Value[0].TryGetProperty("delta", out var d) ? (JsonElement?)d : null;
                                        if (delta is not null)
                                        {
                                            if (delta.Value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                                            {
                                                var rawContent = content.GetString()!;
                                                logger.LogDebug("[CHAT_DEBUG] raw.len={Len} starts_with_think={Starts}", rawContent.Length, rawContent.StartsWith("<think>"));
                                                var cleanContent = ProcessStreamChunk(rawContent, ref inThinkBlock);
                                                collectedContent.Append(cleanContent);
                                                logger.LogDebug("[CHAT_DEBUG] clean.len={Len} changed={Changed}", cleanContent.Length, cleanContent != rawContent);

                                                // Rewrite the SSE line with clean content so the
                                                // client (OpenCode CLI, etc.) never sees raw <think> tags.
                                                if (cleanContent != rawContent)
                                                {
                                                    logger.LogDebug("[CHAT_DEBUG] ENTERING REPLACEMENT BLOCK");
                                                    // Strip think blocks from the raw data string.
                                                    foreach (var tag in AllThinkTagMarkers)
                                                    {
                                                        data = data.Replace(tag, "");
                                                    }
                                                    logger.LogDebug("[CHAT_DEBUG] REPLACEMENT DONE, data.has_think={HasThink}", data.Contains("<think>"));
                                                    line = "data: " + data;
                                                }
                                                else
                                                {
                                                    logger.LogDebug("[CHAT_DEBUG] SKIPPING REPLACEMENT (clean==raw)");
                                                    line = "data: " + data;
                                                }
                                            }
                                        }
                                    }
                                }
                                catch { /* Best-effort parsing */ }
                            }

                            if (data != "[DONE]")
                            {
                                await httpContext.Response.WriteAsync(line + "\n\n", httpContext.RequestAborted);
                                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
                            }
                        }
                    }

                    sw.Stop();
                    await httpContext.Response.WriteAsync("data: [DONE]\n\n", httpContext.RequestAborted);
                    await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);

                    var cost = (promptTokens * genericInputRate) + (completionTokens * genericOutputRate);

                    var usage = new TokenUsage(genericProviderCode, modelName, promptTokens, completionTokens, cost, sw.Elapsed, apiKeyName);
                    await tokenTracker.RecordUsageAsync(usage);

                    var requestLog = new RequestLog
                    {
                        Provider = genericProviderCode,
                        Model = modelName,
                        ApiKeyName = apiKeyName,
                        Messages = requestMessages,
                        ResponseContent = collectedContent.Length > 0 ? collectedContent.ToString() : null,
                        InputTokens = promptTokens,
                        OutputTokens = completionTokens,
                        Cost = cost,
                        Duration = sw.Elapsed,
                        Timestamp = DateTimeOffset.UtcNow,
                        RequestedProviderCode = preferredProvider,
                        RouteKind = "generic",
                        ViaMitmAgent = viaMitmAgent
                    };
                    await requestLogger.TryRecordWithContextAsync(requestLog, httpContext);
                }
                catch (OperationCanceledException)
                {
                    sw.Stop();
                    errorMessage = "Client disconnected";
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    errorMessage = "Chat streaming request failed.";
                    logger.LogWarning("Chat streaming request failed with {ExceptionType}.", ex.GetType().Name);

                    var errorEvent = JsonSerializer.Serialize(new
                    {
                        error = new { message = "Chat streaming request failed.", type = "gateway_error" }
                    }, JsonOpts);
                    await httpContext.Response.WriteAsync($"data: {errorEvent}\n\n", CancellationToken.None);
                    await httpContext.Response.WriteAsync("data: [DONE]\n\n", CancellationToken.None);
                    await httpContext.Response.Body.FlushAsync(CancellationToken.None);

                    var errLog = new RequestLog
                    {
                        Provider = genericProviderCode,
                        Model = modelName,
                        ApiKeyName = apiKeyName,
                        Messages = requestMessages,
                        ResponseContent = collectedContent.Length > 0 ? collectedContent.ToString() : null,
                        InputTokens = 0,
                        OutputTokens = 0,
                        Cost = 0,
                        Duration = sw.Elapsed,
                        Timestamp = DateTimeOffset.UtcNow,
                        IsError = true,
                        ErrorMessage = errorMessage,
                        RequestedProviderCode = preferredProvider,
                        RouteKind = "generic",
                        ViaMitmAgent = viaMitmAgent
                    };
                    await requestLogger.TryRecordWithContextAsync(errLog, httpContext);
                }
                finally
                {
                    streamCounter.Decrement();
                }

                return Results.Empty;
            }

            // ── Non-streaming mode (via MediatR/SendChatHandler) ──
            var command = new SendChatCommand
            {
                // Was the literal "default", which is not a real model code:
                // it resolved to nothing, so routing fell through to the
                // opencode provider regardless of GATEWAY_DEFAULT_MODEL.
                Model = request.Model ?? Configuration.GatewayDefaults.DefaultModel,
                Messages = request.Messages?.Select(m => new ChatMessageDto
                {
                    Role = m.Role ?? "user",
                    Content = m.Content ?? "",
                    ToolCallId = m.ToolCallId,
                    ToolCalls = m.ToolCalls?.Select(tc => new Arkana.Application.Features.Chat.Commands.ToolCallDto
                    {
                        Id = tc.Id,
                        Type = tc.Type,
                        Function = new Arkana.Application.Features.Chat.Commands.ToolCallFunctionDto
                        {
                            Name = tc.Function.Name,
                            Arguments = tc.Function.Arguments
                        }
                    }).ToList()
                }).ToList() ?? [],
                Tools = request.Tools?.Select(t => new Arkana.Application.Features.Chat.Commands.ToolDefinitionDto
                {
                    Type = t.Type,
                    Function = new Arkana.Application.Features.Chat.Commands.ToolFunctionDto
                    {
                        Name = t.Function.Name,
                        Description = t.Function.Description,
                        Parameters = t.Function.Parameters,
                        Strict = t.Function.Strict
                    }
                }).ToList(),
                ToolChoice = request.ToolChoice,
                // Per-key provider pinning (added 2026-08-06). If the API key
                // carries a PreferredProviderCode, route this client's traffic
                // to that upstream instead of the model's owning provider —
                // this is what makes per-client routing configurable from the
                // portal without restricting the client's model list.
                PreferredProvider = httpContext.Items.TryGetValue("ApiKeyPreferredProvider", out var ppObj)
                    ? ppObj as string
                    : null,
                AllowProviderFallback = httpContext.Items.TryGetValue("ApiKeyAllowProviderFallback", out var fallbackObj)
                    && fallbackObj is true,
                ApiKey = rawApiKey,
                ViaMitmAgent = viaMitmAgent
            };

            var result = await mediator.Send(command);

            // A failed completion must NOT be served as HTTP 200 with the error
            // text sitting in the assistant message — clients cannot tell that
            // apart from a real answer. Emit an OpenAI-shaped error object with
            // the proper status (upstream 429 stays 429, auth 401, etc.).
            if (result.IsError)
            {
                var status = result.StatusCode ?? 502;
                return Results.Json(new Dictionary<string, object?>
                {
                    ["error"] = new Dictionary<string, object?>
                    {
                        ["message"] = result.Content,
                        ["type"] = result.ErrorType ?? "gateway_error",
                        ["param"] = null,
                        ["code"] = status
                    }
                }, statusCode: status);
            }

            // Strip thinking blocks from non-streaming responses too
            var nsInThinkBlock = false;
            var nonStreamContent = result.Content is { } contentText
                ? ProcessStreamChunk(contentText, ref nsInThinkBlock)
                : result.Content;

            var responseMsg = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = nonStreamContent
            };

            if (result.ToolCalls is { Count: > 0 })
            {
                responseMsg["tool_calls"] = result.ToolCalls.Select(tc => new Dictionary<string, object?>
                {
                    ["id"] = tc.Id,
                    ["type"] = tc.Type,
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = tc.FunctionName,
                        ["arguments"] = tc.FunctionArguments
                    }
                }).ToList();
            }

            return Results.Ok(new Dictionary<string, object?>
            {
                ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
                ["object"] = "chat.completion",
                ["model"] = result.Model,
                ["choices"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["message"] = responseMsg,
                        ["finish_reason"] = result.ToolCalls is { Count: > 0 } ? "tool_calls" : "stop"
                    }
                },
                ["usage"] = new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = result.InputTokens,
                    ["completion_tokens"] = result.OutputTokens,
                    ["total_tokens"] = result.TotalTokens
                }
            });
        })
        .WithName("CreateChatCompletion")
        .WithTags("Chat")
        .WithOpenApi();

        // Token usage query
        chat.MapGet("/tokens/usage", async ([AsParameters] GetTokenUsageQuery query, IMediator mediator) =>
        {
            var result = await mediator.Send(query);
            return Results.Ok(result);
        })
        .WithName("GetTokenUsage")
        .WithTags("Chat", "Monitoring");

        // OpenAI-compatible models list
        chat.MapGet("/models", async (
            Arkana.Domain.Interfaces.IModelRepository modelRepo,
            HttpContext httpContext) =>
        {
            var tenantId = httpContext.RequestServices.GetRequiredService<ITenantProvider>().TenantId;
            if (tenantId is not { } resolvedTenantId)
                return Results.Problem("Authenticated tenant is required.", statusCode: 401);
            var models = await modelRepo.GetAllAsync(resolvedTenantId);

            // Only advertise models that can ACTUALLY serve a request.
            //
            // Fixed 2026-08-06: this endpoint returned every row in the
            // Models table — 32 models, of which 7 belonged to providers
            // with no credential at all (openai/anthropic, disabled) and
            // so could never succeed. Agents read this catalog to populate
            // their model pickers, so they offered the user a list that was
            // mostly dead. Filter out disabled models AND models whose
            // provider is disabled.
            var visible = models.Where(m => m.IsEnabled && m.Provider.IsEnabled);

            // Scope to what THIS key is allowed to use.
            //
            // Previously unfiltered: a key restricted to a single model
            // still saw the entire catalog and only discovered the
            // restriction when the call came back 403. The allow-list is
            // already resolved by ApiKeyAuthMiddleware. An EMPTY list means
            // "all models" (see ApiKey.CanAccessModel), so only filter when
            // the key actually carries restrictions.
            if (httpContext.Items.TryGetValue("ApiKeyModelIds", out var idsObj)
                && idsObj is Guid[] { Length: > 0 } allowedIds)
            {
                visible = visible.Where(m => allowedIds.Contains(m.Id));
            }

            if (httpContext.Request.Query.ContainsKey("client_version"))
            {
                return Results.Ok(new
                {
                    models = visible.Where(m => m.Code.Equals("gpt-5.5", StringComparison.OrdinalIgnoreCase)).Select(m => new
                    {
                        slug = m.Code,
                        display_name = m.Name,
                        provider = "arkana-gateway",
                        name = m.Code,
                        supported_tools = CodexSupportedTools,
                        experimental_supported_tools = Array.Empty<string>(),
                        supports_parallel_tool_calls = true,
                        reasoning = true,
                        shell_type = "shell_command",
                        visibility = "list",
                        supported_in_api = true,
                        priority = 1,
                        support_verbosity = true,
                        tool_mode = "direct",
                        supported_reasoning_levels = new[]
                        {
                            new { effort = "low", description = "Fast responses with lighter reasoning" },
                            new { effort = "medium", description = "Balanced reasoning" },
                            new { effort = "high", description = "Deep reasoning" },
                            new { effort = "xhigh", description = "Maximum reasoning" }
                        }
                    })
                });
            }

            return Results.Ok(new            {
                @object = "list",
                data = visible.Select(m => new
                {
                    id = m.Code,
                    @object = "model",
                    created = new DateTimeOffset(m.CreatedAt.Year, m.CreatedAt.Month, m.CreatedAt.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                    owned_by = m.Provider.Name
                })
            });
        })
        .WithName("ListModels")
        .WithTags("Chat", "Monitoring");
    }

    /// <summary>
    /// Build a message dictionary from the DTO, including tool_call_id and tool_calls.
    /// </summary>
    private static Dictionary<string, object?> BuildMessageDict(ApplicationDtoMessage m)
    {
        var dict = new Dictionary<string, object?>
        {
            ["role"] = m.Role ?? "user",
            ["content"] = m.Content
        };

        if (m.Role == "tool" && !string.IsNullOrEmpty(m.ToolCallId))
        {
            dict["tool_call_id"] = m.ToolCallId;
        }

        if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
        {
            dict["tool_calls"] = m.ToolCalls.Select(tc => new Dictionary<string, object?>
            {
                ["id"] = tc.Id,
                ["type"] = tc.Type,
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tc.Function.Name,
                    ["arguments"] = tc.Function.Arguments
                }
            }).ToList();
        }

        return dict;
    }

    /// <summary>
    /// Reasoning/thinking tag variants that models emit inline in content.
    /// Must be stripped from streaming output so clients don't display them literally.
    /// </summary>
    private static readonly string[] OpenThinkTags =
        ["<think>", "<THINKING>", "<thinking>", "<thought>", "<reasoning>", "<REASONING_SCRATCHPAD>"];

    private static readonly string[] CloseThinkTags =
        ["</think>", "</THINKING>", "</thinking>", "</thought>", "</reasoning>", "</REASONING_SCRATCHPAD>"];

    /// <summary>
    /// All think tag markers (opening and closing) for raw string-level removal
    /// from SSE data. Used as a fallback when JSON replacement fails.
    /// </summary>
    private static readonly string[] AllThinkTagMarkers =
        ["<think>", "</think>", "<thinking>", "</thinking>", "<THINKING>", "</THINKING>",
         "<thought>", "</thought>", "<reasoning>", "</reasoning>",
         "<REASONING_SCRATCHPAD>", "</REASONING_SCRATCHPAD>"];

    /// <summary>
    /// Processes a streaming content chunk through a think-tag state machine.
    /// Maintains <c>inThinkBlock</c> state across calls so reasoning blocks
    /// that span multiple SSE chunks are correctly stripped.
    /// </summary>
    private static string ProcessStreamChunk(string chunk, ref bool inThinkBlock)
    {
        if (string.IsNullOrEmpty(chunk)) return chunk;

        var result = new System.Text.StringBuilder(chunk.Length);
        var remaining = chunk.AsSpan();

        while (remaining.Length > 0)
        {
            if (inThinkBlock)
            {
                // Find the earliest closing tag in the remaining text
                var earliestClose = int.MaxValue;
                var closeTagLen = 0;
                foreach (var tag in CloseThinkTags)
                {
                    var idx = remaining.IndexOf(tag.AsSpan(), StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0 && idx < earliestClose)
                    {
                        earliestClose = idx;
                        closeTagLen = tag.Length;
                    }
                }

                if (earliestClose < int.MaxValue)
                {
                    // Discard everything up to and including the closing tag
                    remaining = remaining[(earliestClose + closeTagLen)..];
                    inThinkBlock = false;
                }
                else
                {
                    // Still inside a thinking block — discard the whole chunk
                    remaining = [];
                }
            }
            else
            {
                // Find the earliest opening tag in the remaining text
                var earliestOpen = int.MaxValue;
                var openTagLen = 0;
                foreach (var tag in OpenThinkTags)
                {
                    var idx = remaining.IndexOf(tag.AsSpan(), StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0 && idx < earliestOpen)
                    {
                        earliestOpen = idx;
                        openTagLen = tag.Length;
                    }
                }

                if (earliestOpen < int.MaxValue)
                {
                    // Emit everything before the opening tag
                    result.Append(remaining[..earliestOpen]);
                    // Skip the opening tag and enter thinking block
                    remaining = remaining[(earliestOpen + openTagLen)..];
                    inThinkBlock = true;
                }
                else
                {
                    // No opening tag in this chunk — emit all remaining text
                    result.Append(remaining);
                    remaining = [];
                }
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Streams a ChatGPT / Codex completion to the client as OpenAI-compatible SSE.
    /// ChatGPT's /codex endpoint emits the standard chat/completions SSE shape
    /// (choices[0].delta.content), so we pipe it through (with the same think-tag
    /// stripping applied to other providers) and record usage on completion.
    /// </summary>
    private static Arkana.Domain.Interfaces.ChatRequest BuildChatRequest(
        ChatCompletionRequest request,
        string? preferredProviderCode,
        Guid? preferredProviderId = null,
        Guid? preferredProviderAccountId = null,
        string? preferredProviderAccountCode = null,
        Guid tenantId = default) => new()
    {
        Model = request.Model ?? Arkana.Gateway.Api.Configuration.GatewayDefaults.DefaultModel,
        Messages = request.Messages?.Select(m => new Arkana.Domain.Interfaces.ChatMessage
        {
            Role = m.Role ?? "user",
            Content = m.Content ?? "",
            ToolCallId = m.ToolCallId,
            ToolCalls = m.ToolCalls?.Select(tc => new Arkana.Domain.Interfaces.ToolCall
            {
                Id = tc.Id ?? "",
                Type = tc.Type ?? "function",
                Function = new Arkana.Domain.Interfaces.ToolCallFunction
                {
                    Name = tc.Function.Name ?? "",
                    Arguments = tc.Function.Arguments ?? "{}"
                }
            }).ToList()
        }).ToList() ?? [],
        Tools = request.Tools?.Select(t => new Arkana.Domain.Interfaces.ToolDefinition
        {
            Type = t.Type,
            Function = new Arkana.Domain.Interfaces.ToolFunction
            {
                Name = t.Function.Name,
                Description = t.Function.Description,
                Parameters = t.Function.Parameters,
                Strict = t.Function.Strict
            }
        }).ToList(),
        ToolChoice = request.ToolChoice,
        PreferredProviderCode = preferredProviderCode,
        PreferredProviderId = preferredProviderId,
        PreferredProviderAccountId = preferredProviderAccountId,
        PreferredProviderAccountCode = preferredProviderAccountCode,
        TenantId = tenantId
    };

    private static async Task<IResult> StreamGeminiBrokerAsync(
        ChatCompletionRequest request,
        IStreamingChatCompletionService streamingBroker,
        HttpContext httpContext,
        ITokenTracker tokenTracker,
        IRequestLogger requestLogger,
        ActiveStreamCounter streamCounter,
        Arkana.Domain.Interfaces.IAiProviderRepository providerRepo,
        Arkana.Domain.Entities.AiProvider? configuredProvider,
        string? preferredProviderCode,
        Guid? preferredProviderId,
        Guid? preferredProviderAccountId,
        string? preferredProviderAccountCode,
        string? viaMitmAgent)
    {
        var streamResult = await streamingBroker.CompleteStreamingAsync(
            BuildChatRequest(request, preferredProviderCode, preferredProviderId,
                preferredProviderAccountId, preferredProviderAccountCode,
                httpContext.RequestServices.GetRequiredService<ITenantProvider>().TenantId
                    ?? throw new InvalidOperationException("Authenticated tenant is required.")), httpContext.RequestAborted);
        if (!streamResult.IsSuccess || streamResult.Stream is null)
        {
            var status = streamResult.Result.UpstreamStatus is >= 400 and <= 599
                ? streamResult.Result.UpstreamStatus
                : 503;
            await streamResult.DisposeAsync();
            return Results.Problem("Gemini subscription streaming failed.", statusCode: status);
        }

        await using (streamResult)
        {
            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers.Connection = "keep-alive";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no";
            streamCounter.Increment();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var collected = new System.Text.StringBuilder();
            var inThinkBlock = false;
            var terminalFrameStarted = false;
            var upstreamCompleted = false;
            var promptTokens = 0;
            var completionTokens = 0;
            var nativeStream = streamResult.Result.RouteKind.Equals("native", StringComparison.OrdinalIgnoreCase);
            var nativeState = new Arkana.Infrastructure.AI.GeminiNativeStreamState();

            try
            {
                using var reader = new StreamReader(streamResult.Stream, leaveOpen: true);
                while (await reader.ReadLineAsync(httpContext.RequestAborted) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                        continue;

                    var data = line[5..].TrimStart();
                    if (data.Equals("[DONE]", StringComparison.Ordinal))
                    {
                        upstreamCompleted = true;
                        continue;
                    }

                    if (nativeStream)
                    {
                        if (!Arkana.Infrastructure.AI.GeminiNativeStreamTranslator.TryTranslate(
                                data, request.Model ?? "gemini", nativeState, out var translated,
                                out var nativePromptTokens, out var nativeCompletionTokens))
                            throw new InvalidDataException("The native Gemini returned an invalid streaming envelope.");
                        promptTokens = Math.Max(promptTokens, nativePromptTokens);
                        completionTokens = Math.Max(completionTokens, nativeCompletionTokens);
                        if (nativeState.SawFinish)
                            upstreamCompleted = true;
                        if (translated is null)
                            continue;
                        data = translated;
                    }

                    using var document = JsonDocument.Parse(data);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || root.TryGetProperty("error", out _))
                        throw new InvalidDataException("The Gemini broker returned an invalid streaming envelope.");

                    if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var promptValue))
                            promptTokens = promptValue;
                        if (usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt32(out var completionValue))
                            completionTokens = completionValue;
                    }

                    var forwarded = data;
                    var mutable = JsonNode.Parse(data)?.AsObject();
                    if (mutable?["choices"] is JsonArray choices && choices.Count > 0
                        && choices[0] is JsonObject choice
                        && choice["delta"] is JsonObject delta)
                    {
                        if (delta["content"] is JsonValue contentValue && contentValue.TryGetValue<string>(out var rawContent))
                        {
                            var cleanContent = ProcessStreamChunk(rawContent, ref inThinkBlock);
                            collected.Append(cleanContent);
                            delta["content"] = cleanContent;
                        }

                        forwarded = mutable.ToJsonString(JsonOpts);
                    }

                    await httpContext.Response.WriteAsync("data: " + forwarded + "\n\n", httpContext.RequestAborted);
                    await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
                }

                if (!upstreamCompleted)
                    throw new InvalidDataException("The Gemini broker closed the stream before its terminal marker.");

                terminalFrameStarted = true;
                await httpContext.Response.WriteAsync("data: [DONE]\n\n", httpContext.RequestAborted);
                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
                streamResult.MarkCompleted();

                stopwatch.Stop();
                var providerCode = streamResult.Result.ResolvedProviderAccountCode
                    ?? configuredProvider?.Code
                    ?? "gemini-subscription";
                var model = streamResult.Result.Model ?? request.Model ?? "gemini";
                var tenantId = httpContext.RequestServices.GetRequiredService<ITenantProvider>().TenantId
                    ?? throw new InvalidOperationException("Authenticated tenant is required.");
                var actualProvider = await providerRepo.GetByCodeAsync(providerCode, tenantId, CancellationToken.None)
                    ?? configuredProvider;
                var cost = (promptTokens * (actualProvider?.CostPerInputToken ?? 0m))
                    + (completionTokens * (actualProvider?.CostPerOutputToken ?? 0m));
                await tokenTracker.RecordUsageAsync(new TokenUsage(
                    providerCode, model, promptTokens, completionTokens, cost, stopwatch.Elapsed,
                    httpContext.Items.TryGetValue("ApiKeyName", out var apiName) ? apiName?.ToString() : null));
                await requestLogger.TryRecordWithContextAsync(new RequestLog
                {
                    Provider = providerCode,
                    Model = model,
                    ApiKeyName = httpContext.Items.TryGetValue("ApiKeyName", out var logName) ? logName?.ToString() : null,
                    Messages = request.Messages?.Select(m => new ChatMessage { Role = m.Role ?? "user", Content = m.Content ?? "" }).ToList() ?? [],
                    ResponseContent = collected.ToString(),
                    InputTokens = promptTokens,
                    OutputTokens = completionTokens,
                    Cost = cost,
                    Duration = stopwatch.Elapsed,
                    Timestamp = DateTimeOffset.UtcNow,
                    RequestedProviderCode = preferredProviderCode,
                    RequestedProviderAccountCode = preferredProviderAccountCode,
                    ViaMitmAgent = viaMitmAgent,
                    RouteKind = streamResult.Result.RouteKind,
                    ResolvedProviderAccountId = streamResult.Result.ResolvedProviderAccountId,
                    ResolvedProviderAccountCode = streamResult.Result.ResolvedProviderAccountCode,
                }, httpContext);
            }
            catch (OperationCanceledException)
            {
                // Client disconnect or request cancellation is not a successful completion.
            }
            catch
            {
                if (!terminalFrameStarted)
                {
                    terminalFrameStarted = true;
                    await httpContext.Response.WriteAsync(
                        "data: {\"error\":{\"message\":\"Gemini stream failed.\",\"type\":\"gateway_error\"}}\n\ndata: [DONE]\n\n",
                        CancellationToken.None);
                    await httpContext.Response.Body.FlushAsync(CancellationToken.None);
                }
            }
            finally
            {
                streamCounter.Decrement();
            }
        }

        return Results.Empty;
    }

    private static async Task<IResult> StreamChatGptAsync(
        ChatCompletionRequest request,
        Arkana.Infrastructure.AI.ChatGptCodexChatService chatGptSvc,
        HttpContext httpContext,
        ITokenTracker tokenTracker,
        IRequestLogger requestLogger,
        ActiveStreamCounter streamCounter,
        Arkana.Domain.Interfaces.IAiProviderRepository providerRepo,
        string? apiKeyName,
        string modelName,
        string? preferredProviderCode,
        bool allowProviderFallback,
        string? viaMitmAgent)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tenantId = httpContext.RequestServices.GetRequiredService<ITenantProvider>().TenantId
            ?? throw new InvalidOperationException("Authenticated tenant is required.");
        var chatRequest = new Arkana.Domain.Interfaces.ChatRequest
        {
            Model = request.Model ?? Arkana.Gateway.Api.Configuration.GatewayDefaults.DefaultModel,
            Messages = request.Messages?.Select(m => new Arkana.Domain.Interfaces.ChatMessage
            {
                Role = m.Role ?? "user",
                Content = m.Content ?? "",
                ToolCallId = m.ToolCallId,
                ToolCalls = m.ToolCalls?.Select(tc => new Arkana.Domain.Interfaces.ToolCall
                {
                    Id = tc.Id ?? "",
                    Type = tc.Type ?? "function",
                    Function = new Arkana.Domain.Interfaces.ToolCallFunction
                    {
                        Name = tc.Function.Name ?? "",
                        Arguments = tc.Function.Arguments ?? "{}"
                    }
                }).ToList()
            }).ToList() ?? [],
            // Forward tool definitions + tool_choice so the model can actually
            // call tools (previously dropped here, so ChatGPT never saw them).
            Tools = request.Tools?.Select(t => new Arkana.Domain.Interfaces.ToolDefinition
            {
                Type = t.Type,
                Function = new Arkana.Domain.Interfaces.ToolFunction
                {
                    Name = t.Function.Name,
                    Description = t.Function.Description,
                    Parameters = t.Function.Parameters,
                    Strict = t.Function.Strict,
                }
            }).ToList(),
            ToolChoice = request.ToolChoice,
            PreferredProviderCode = preferredProviderCode,
            AllowProviderFallback = allowProviderFallback,
            TenantId = tenantId,
        };

        var (stream, status, error) = await chatGptSvc.CompleteStreamingAsync(chatRequest, httpContext.RequestAborted);

        if (stream is null)
        {
            var code = status ?? 502;
            return Results.Json(new Dictionary<string, object?>
            {
                ["error"] = new Dictionary<string, object?>
                {
                    ["message"] = error ?? "ChatGPT streaming failed.",
                    ["type"] = "gateway_error",
                    ["param"] = null,
                    ["code"] = code
                }
            }, statusCode: code);
        }

        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";
        streamCounter.Increment();

        int promptTokens = 0, completionTokens = 0;
        var collected = new System.Text.StringBuilder();
        var inThink = false;
        var toolCallIds = new Dictionary<int, string>();
        var toolCallNames = new Dictionary<int, string>();

        try
        {
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(httpContext.RequestAborted) is { } line)
            {
                if (string.IsNullOrEmpty(line)) continue;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                var data = line[6..];
                if (data == "[DONE]")
                {
                    await httpContext.Response.WriteAsync("data: [DONE]\n\n", httpContext.RequestAborted);
                    await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    {
                        if (u.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
                        if (u.TryGetProperty("completion_tokens", out var ct)) completionTokens = ct.GetInt32();
                    }
                    if (root.TryGetProperty("choices", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0)
                    {
                        var delta = c[0].TryGetProperty("delta", out var d) ? (JsonElement?)d : null;
                        if (delta is not null && delta.Value.TryGetProperty("tool_calls", out var toolCalls)
                            && toolCalls.ValueKind == JsonValueKind.Array)
                        {
                            data = NormalizeToolCallStreamData(data, toolCallIds, toolCallNames);
                            line = "data: " + data;
                        }
                        if (delta is not null && delta.Value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        {
                            var raw = content.GetString()!;
                            var clean = ProcessStreamChunk(raw, ref inThink);
                            collected.Append(clean);
                            line = "data: " + data;
                        }
                    }
                }
                catch { /* best-effort: forward raw line */ }

                if (data != "[DONE]")
                {
                    await httpContext.Response.WriteAsync(line + "\n\n", httpContext.RequestAborted);
                    await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected — stop gracefully
        }
        finally
        {
            streamCounter.Decrement();
        }

        sw.Stop();
        var actualProviderCode = chatGptSvc.LastSelectedAccountCode
            ?? preferredProviderCode
            ?? "chatgpt";
        var actualProvider = await providerRepo.GetByCodeAsync(actualProviderCode, tenantId, httpContext.RequestAborted);
        var inputRate = actualProvider?.CostPerInputToken ?? 0m;
        var outputRate = actualProvider?.CostPerOutputToken ?? 0m;
        var cost = (promptTokens * inputRate) + (completionTokens * outputRate);
        await tokenTracker.RecordUsageAsync(new TokenUsage(
            actualProviderCode, modelName, promptTokens, completionTokens, cost, sw.Elapsed, apiKeyName));
        await requestLogger.TryRecordWithContextAsync(new RequestLog
        {
            Provider = actualProviderCode,
            Model = modelName,
            ApiKeyName = apiKeyName,
            Messages = chatRequest.Messages.Select(m => new ChatMessage { Role = m.Role, Content = m.Content }).ToList(),
            ResponseContent = collected.Length > 0 ? collected.ToString() : null,
            InputTokens = promptTokens,
            OutputTokens = completionTokens,
            Cost = cost,
            Duration = sw.Elapsed,
            Timestamp = DateTimeOffset.UtcNow,
            TenantId = tenantId,
            RequestedProviderCode = preferredProviderCode,
            RouteKind = "chatgpt",
            ViaMitmAgent = viaMitmAgent
            }, httpContext);

            return Results.Empty;
            }

            private static string NormalizeToolCallStreamData(string data, Dictionary<int, string> ids, Dictionary<int, string> names)
            {
            try
            {
                var root = JsonNode.Parse(data)?.AsObject();
                var choices = root?["choices"]?.AsArray();
                var delta = choices is { Count: > 0 } ? choices[0]?["delta"]?.AsObject() : null;
                var calls = delta?["tool_calls"]?.AsArray();
                if (calls is null) return data;
                for (var i = 0; i < calls.Count; i++)
                {
                    if (calls[i] is not JsonObject call) continue;
                    var index = i;
                    if (call["index"] is JsonValue indexValue && indexValue.TryGetValue<int>(out var parsedIndex)) index = parsedIndex;
                    var isNewCall = !ids.ContainsKey(index);
                    var id = ExtractJsonString(call["id"]);
                    if (string.IsNullOrWhiteSpace(id)) id = ids.TryGetValue(index, out var previousId) ? previousId : $"call_{index}";
                    ids[index] = id;
                    call["id"] = id;
                    call["index"] = index;
                    var function = call["function"] as JsonObject ?? new JsonObject();
                    call["function"] = function;
                    var name = ExtractJsonString(function["name"]);
                    if (!string.IsNullOrWhiteSpace(name)) names[index] = name;
                    else if (!names.ContainsKey(index)) names[index] = "unknown_tool";
                    if (isNewCall && !function.ContainsKey("name") && names.TryGetValue(index, out var knownName)) function["name"] = knownName;
                }
                return root!.ToJsonString(JsonOpts);
            }
            catch { return data; }
            }

            private static bool IsGeminiProviderCode(string? providerCode)
                => providerCode is not null
                    && (providerCode.Equals("gemini", StringComparison.OrdinalIgnoreCase)
                        || providerCode.Equals("gemini-subscription", StringComparison.OrdinalIgnoreCase)
                        || providerCode.StartsWith("gemini-acc", StringComparison.OrdinalIgnoreCase));

            private static bool LooksLikeGeminiModel(string model)
            {
                var leaf = model.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? model;
                return leaf.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
                    || leaf.StartsWith("gemma-", StringComparison.OrdinalIgnoreCase);
            }

            private static string? ExtractJsonString(JsonNode? node)
            {
            if (node is null) return null;
            try { if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text; } catch { }
            var raw = node.ToJsonString();
            return raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;
            }
            }


// OpenAI-compatible request DTO — kept in this file for HTTP deserialization
/// <summary>
/// OpenAI-compatible chat completion request DTO.
/// Note: Tool-related nested types use Application-layer DTOs (from Commands namespace).
/// </summary>
public sealed record ChatCompletionRequest
{
    public string? Model { get; init; }
    public IReadOnlyList<ApplicationDtoMessage>? Messages { get; init; }
    public IReadOnlyList<ApplicationDtoToolDefinition>? Tools { get; init; }
    public JsonElement? ToolChoice { get; init; }
    public bool? Stream { get; init; }
}

/// <summary>
/// A single chat message used for HTTP deserialization and streaming body construction.
/// Uses Application-layer DTOs for nested tool types.
/// </summary>
public sealed record ApplicationDtoMessage
{
    public string? Role { get; init; }

    public string? Content { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("tool_calls")]
    public IReadOnlyList<Arkana.Application.Features.Chat.Commands.ToolCallDto>? ToolCalls { get; init; }
}

/// <summary>
/// Tool definition — references Application-layer DTO.
/// </summary>
public sealed record ApplicationDtoToolDefinition
{
    public string Type { get; init; } = "function";
    public Arkana.Application.Features.Chat.Commands.ToolFunctionDto Function { get; init; } = new();
}
