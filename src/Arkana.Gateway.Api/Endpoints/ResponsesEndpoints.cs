using Arkana.Application.Features.Chat.Commands;
using Arkana.Application.Features.Chat.Queries;
using Arkana.Domain.Interfaces;
using Arkana.Gateway.Api.Authorization;
using Arkana.Domain.ValueObjects;
using Arkana.Gateway.Api.Services;
using MediatR;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Arkana.Gateway.Api.Endpoints;

/// <summary>
/// Maps OpenAI Responses API (v1/responses) endpoint, translating it to
/// Chat Completions API calls internally. Required by Codex CLI/Desktop which
/// speaks only the Responses API wire format.
/// Supports: text, tool_calls, function_call_output — full round-trip.
/// </summary>
public static class ResponsesEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void MapResponsesEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapPost("/responses", async (HttpContext httpContext,
            IConfiguration config, IHttpClientFactory httpClientFactory,
            ITokenTracker tokenTracker, IRequestLogger requestLogger,
            ActiveStreamCounter streamCounter,
            IAiProviderRepository providerRepo,
            Arkana.Domain.Services.ICredentialVault vault,
            IEnumerable<IChatCompletionService> chatServices) =>
        {
            var loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("Arkana.Gateway.Api.ResponsesEndpoints");

            // ── Parse the Responses API request body ──────────────
            ResponsesRequest? body;
            try
            {
                httpContext.Request.EnableBuffering();
                using var reader = new StreamReader(httpContext.Request.Body, leaveOpen: true);
                var raw = await reader.ReadToEndAsync();
                httpContext.Request.Body.Position = 0;

                if (string.IsNullOrWhiteSpace(raw))
                {
                    return Results.Json(
                        new { type = "error", error = new { message = "Empty request body" } },
                        JsonOpts, statusCode: 400);
                }

                body = JsonSerializer.Deserialize<ResponsesRequest>(raw, JsonOpts);
                if (body is null)
                {
                    return Results.Json(
                        new { type = "error", error = new { message = "Failed to deserialize request body" } },
                        JsonOpts, statusCode: 400);
                }
            }
            catch (JsonException)
            {
                logger.LogWarning("[Responses API] Invalid JSON request body.");
                return Results.Json(
                    new { type = "error", error = new { message = "Invalid JSON request body.", type = "invalid_request_error" } },
                    JsonOpts, statusCode: 400);
            }

            if (body is null)
            {
                return Results.Json(
                    new { type = "error", error = new { message = "Empty request body" } },
                    JsonOpts, statusCode: 400);
            }

            var model = body.Model ?? Configuration.GatewayDefaults.DefaultModel;
            var requestedModel = model;
            // Only override model if it's one we know won't work (e.g., obsolete Codex cached names)
            // Otherwise, let the client-requested model pass through so tools work properly.
            var isStream = body.Stream ?? false;

            // ── Resolve the provider for the requested model ─────
            // Use ModelRouter to find the right provider, or default to OpenCode
            string upstreamBaseUrl = "https://opencode.ai/zen/go/v1";
            string? upstreamApiKey = null;

            // Provider facts surfaced to the metering/logging below. Historically
            // these were hardcoded to "OpenCode" + a fixed price (see
            // code-review-2026-08-06, M1), which produced wrong per-provider
            // billing and corrupted the dashboard's per-provider analytics. They
            // are now derived from the resolved provider.
            string resolvedProviderCode = "opencode";
            decimal resolvedCostPerInput = 0.00000014m;
            decimal resolvedCostPerOutput = 0.00000028m;

            // ── Extract auth ─────────────────────────────────────
            var rawApiKey = Middleware.ApiKeyExtractor.Extract(httpContext.Request);
            string? apiKeyName = httpContext.Items.TryGetValue("ApiKeyName", out var nameObj) ? nameObj?.ToString() : null;

            var preferredProviderCode = httpContext.Items.TryGetValue("ApiKeyPreferredProvider", out var preferredObj)
                ? preferredObj?.ToString()
                : null;
            var allowProviderFallback = httpContext.Items.TryGetValue("ApiKeyAllowProviderFallback", out var fallbackObj)
                && fallbackObj is true;
            var requestedAccountId = httpContext.Items.TryGetValue(
                "ApiKeyPreferredProviderAccountId", out var accountIdObject)
                && accountIdObject is Guid accountId
                ? accountId
                : (Guid?)null;
            var targetPlanner = httpContext.RequestServices.GetService<IProviderTargetPlanner>();
            var tenantProvider = httpContext.RequestServices.GetService<ITenantProvider>();
            if (tenantProvider?.TenantId is not { } tenantId)
                return Results.Problem("Authenticated tenant is required.", statusCode: 401);
            ProviderTarget? geminiTarget = null;
            Arkana.Domain.Entities.AiProvider? targetProvider = null;

            // ── Resolve provider from DB based on model ────────────
            // Look up which provider owns this model, fall back to OpenCode
            try
            {
                var allProviders = await providerRepo.GetAllAsync(tenantId, httpContext.RequestAborted);
                var modelRepo2 = httpContext.RequestServices.GetRequiredService<IModelRepository>();
                var allModels = await modelRepo2.GetAllAsync(tenantId, httpContext.RequestAborted);
                var pinnedProvider = preferredProviderCode is null
                    ? null
                    : allProviders.FirstOrDefault(p => p.IsEnabled
                        && (p.Code.Equals(preferredProviderCode, StringComparison.OrdinalIgnoreCase)
                            || p.Name.Equals(preferredProviderCode, StringComparison.OrdinalIgnoreCase)));
                var stableGeminiAlias = preferredProviderCode?.Equals(
                    "gemini-subscription", StringComparison.OrdinalIgnoreCase) == true;
                if (preferredProviderCode is not null
                    && pinnedProvider is null
                    && IsGeminiProviderCode(preferredProviderCode)
                    && !stableGeminiAlias)
                    return Results.Problem("Pinned Gemini provider is unavailable.", statusCode: 503);
                if (!allowProviderFallback && preferredProviderCode is not null && pinnedProvider is null
                    && !stableGeminiAlias)
                    return Results.Problem($"Pinned provider '{preferredProviderCode}' is unavailable.", statusCode: 503);

                // Find the model config to get its provider. If this key is pinned,
                // select the duplicate row belonging to that provider first.
                var modelConfig = allModels
                    .Where(m => m.Code.Equals(model, StringComparison.OrdinalIgnoreCase)
                        && m.IsEnabled && m.Provider.IsEnabled
                        && ApiKeyModelAuthorization.IsAllowed(httpContext, m)
                        && (preferredProviderCode is null
                            || m.Provider.Code.Equals(preferredProviderCode, StringComparison.OrdinalIgnoreCase)
                            || m.Provider.Name.Equals(preferredProviderCode, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(m => preferredProviderCode is not null
                        && m.Provider?.Code.Equals(preferredProviderCode, StringComparison.OrdinalIgnoreCase) == true)
                    .ThenBy(m => m.Provider?.Code, StringComparer.Ordinal)
                    .ThenBy(m => m.Name, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (ApiKeyModelAuthorization.IsRestricted(httpContext) && modelConfig is null)
                    return Results.Problem("API key does not have access to the requested model.", statusCode: 403);

                Arkana.Domain.Entities.AiProvider? modelProvider = null;
                if (modelConfig is not null)
                {
                    modelProvider = allProviders.FirstOrDefault(p => p.Id == modelConfig.ProviderId);
                    // Pre-flight remap: if model routes to opencode but isn't the sandbox-safe
                    // model, remap so upstream doesn't 403 (the opencode sandbox key only
                    // authorizes one model; others return RegionError/403).
                    // Now opt-out via OPENCODE_FORCE_SANDBOX_MODEL=0 — this silently overrides
                    // the caller's model choice, which is wrong once the key/quota allows more.
                    if (Configuration.GatewayDefaults.ForceOpenCodeSandboxModel &&
                        modelProvider is not null &&
                        modelProvider.Code.Equals("opencode", StringComparison.OrdinalIgnoreCase) &&
                        !model.Equals(Configuration.GatewayDefaults.OpenCodeSandboxModel, StringComparison.OrdinalIgnoreCase))
                    {
                        var originalModel = model;
                        model = Configuration.GatewayDefaults.OpenCodeSandboxModel;
                        logger.LogWarning(
                            "[Responses API] Model '{Original}' resolves to opencode provider; remapping to '{Remapped}' to avoid upstream 401.",
                            originalModel, model);
                        // Re-resolve model config for the remapped name
                        modelConfig = allModels.FirstOrDefault(m =>
                            m.Code.Equals(model, StringComparison.OrdinalIgnoreCase)
                            && ApiKeyModelAuthorization.IsAllowed(httpContext, m));
                        if (modelConfig is not null)
                            modelProvider = allProviders.FirstOrDefault(p => p.Id == modelConfig.ProviderId);
                        else if (ApiKeyModelAuthorization.IsRestricted(httpContext))
                            return Results.Problem("API key does not have access to the routed model.", statusCode: 403);
                    }
                }
                else if ((allowProviderFallback || preferredProviderCode is null)
                    && !LooksLikeGeminiModel(requestedModel)
                    && !IsGeminiProviderCode(preferredProviderCode))
                {
                    // Requested model is NOT in the gateway model table (e.g. Codex-internal
                    // names like "gpt-5.6-luna"). Forwarding the unknown name verbatim to the
                    // upstream yields a 401 and severs the SSE stream mid-turn (Codex "cuts off"
                    // with no tool call). Remap to a known working model so the request survives.
                    var fallbackModel = Configuration.GatewayDefaults.DefaultModel;
                    logger.LogWarning(
                        "[Responses API] Model '{Model}' not in gateway table; remapping to '{Fallback}' (sandbox-safe mimo-v2.5).",
                        model, fallbackModel);
                    model = fallbackModel;
                    modelConfig = allModels.FirstOrDefault(m =>
                        m.Code.Equals(model, StringComparison.OrdinalIgnoreCase));
                    if (modelConfig is not null)
                        modelProvider = allProviders.FirstOrDefault(p => p.Id == modelConfig.ProviderId);
                }

                var explicitGeminiRequest = IsGeminiProviderCode(preferredProviderCode)
                    || IsGeminiProviderCode(modelProvider?.Code);
                var geminiOwnedRequest = explicitGeminiRequest
                    || (modelConfig is null && LooksLikeGeminiModel(requestedModel));
                if (geminiOwnedRequest
                    && modelConfig is null
                    && !stableGeminiAlias
                    && !explicitGeminiRequest)
                    return Results.Problem("Gemini model routing could not be resolved safely.", statusCode: 503);

                // Strict keys never leave their pin. Cross-provider selection is
                // reachable only for explicitly fallback-enabled keys.
                targetProvider = (preferredProviderCode is not null
                    ? pinnedProvider
                    : modelProvider)
                    ?? allProviders.FirstOrDefault(p =>
                        p.Code.Equals("opencode", StringComparison.OrdinalIgnoreCase))
                    ?? allProviders.FirstOrDefault(p => p.IsEnabled);

                if (geminiOwnedRequest && stableGeminiAlias)
                {
                    resolvedProviderCode = "gemini-subscription";
                    targetProvider = null;
                }
                else if (geminiOwnedRequest && (targetProvider is null || !IsGeminiProviderCode(targetProvider.Code)))
                {
                    return Results.Problem("Gemini provider routing could not be resolved safely.", statusCode: 503);
                }

                if (explicitGeminiRequest && !stableGeminiAlias)
                {
                    if (targetPlanner is null || targetProvider is null)
                        return Results.Problem("Gemini provider ownership is unavailable.", statusCode: 503);

                    geminiTarget = await targetPlanner.ResolveAsync(
                        tenantId, targetProvider, model, requestedAccountId, httpContext.RequestAborted);
                    if (!geminiTarget.IsResolved)
                        return Results.Problem("Gemini provider ownership is invalid.", statusCode: 503);
                }

                if (targetProvider is not null)
                {
                    if (!string.IsNullOrEmpty(targetProvider.BaseUrl))
                        upstreamBaseUrl = targetProvider.BaseUrl;
                    // SECURITY: decrypt the sealed ApiKey just-in-time.
                    var decrypted = targetProvider.DecryptApiKey(vault);
                    if (!string.IsNullOrEmpty(decrypted))
                        upstreamApiKey = decrypted;

                    // Surface the resolved provider to metering/logging so the
                    // record reflects where the request actually went (M1 fix).
                    resolvedProviderCode = targetProvider.Code;
                    resolvedCostPerInput = targetProvider.CostPerInputToken;
                    resolvedCostPerOutput = targetProvider.CostPerOutputToken;
                }
            }
            catch when ((allowProviderFallback || preferredProviderCode is null)
                && !IsGeminiProviderCode(preferredProviderCode)
                && !LooksLikeGeminiModel(requestedModel))
            {
                // Explicitly fallback-enabled/unpinned compatibility path only.
                upstreamBaseUrl = config["ProviderOptions:OpenCode:BaseUrl"] ?? upstreamBaseUrl;
                var cfgKey = config["ProviderOptions:OpenCode:ApiKey"];
                upstreamApiKey = !string.IsNullOrEmpty(cfgKey)
                    ? cfgKey
                    : Environment.GetEnvironmentVariable("OPENCODE_GO_API_KEY");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Responses API] Provider routing failed closed.");
                return Results.Problem("Provider routing is unavailable.", statusCode: 503);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var collectedContent = new System.Text.StringBuilder();
            var responseId = $"resp_{Guid.NewGuid():N}";
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // ── Build Chat Completions messages from Responses input ─
            var messages = new List<object>();

            // instructions → system message
            if (!string.IsNullOrEmpty(body.Instructions))
            {
                messages.Add(new { role = "system", content = body.Instructions });
            }

            // Track the most recent assistant message for tool_calls continuity
            object? pendingAssistantMsg = null;

            // Accumulated tool_calls for the CURRENT parallel-call group. Flushed as a
            // single assistant message when the next item is no longer a function_call.
            var pendingToolCalls = new List<object>();

            // input can be string or array of items
            if (body.Input is { } inputEl)
            {
                if (inputEl.ValueKind == JsonValueKind.String)
                {
                    messages.Add(new { role = "user", content = inputEl.GetString() ?? "" });
                }
                else if (inputEl.ValueKind == JsonValueKind.Array)
                {
                    var inputElValue = inputEl.EnumerateArray().ToArray();
                    for (int idx = 0; idx < inputElValue.Length; idx++)
                    {
                    var item = inputElValue[idx];
                        var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                        var role = item.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";

                        // Map Responses API roles to Chat Completions roles
                        role = role switch
                        {
                            "developer" => "system",
                            "assistant" => "assistant",
                            "tool" => "tool",
                            _ => "user"
                        };

                        if (type == "message" || (string.IsNullOrEmpty(type) && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("content", out _)))
                        {
                            // Handles both typed {"type":"message",...} items AND bare
                            // {"role":"user","content":...} items that clients like Codex
                            // send on turn 2 of a tool loop (no "type" field). Dropping
                            // these silently produced an assistant-first conversation that
                            // the upstream (Console Go / DeepSeek) rejects with HTTP 400.
                            if (item.TryGetProperty("content", out var contentEl))
                            {
                                var text = ExtractTextFromContent(contentEl);
                                if (role == "assistant")
                                {
                                    // Need to check if this assistant message has tool_calls
                                    pendingAssistantMsg = new
                                    {
                                        role = "assistant",
                                        content = text,
                                        tool_calls = ExtractToolCallsFromItem(item)
                                    };
                                    messages.Add(pendingAssistantMsg);
                                }
                                else
                                {
                                    messages.Add(new { role, content = text });
                                }
                            }
                        }
                        else if (type == "input_text")
                        {
                            var text = item.TryGetProperty("text", out var txtEl) ? txtEl.GetString() ?? "" : "";
                            messages.Add(new { role, content = text });
                        }
                        else if (type == "function_call_output")
                        {
                            // Tool result from Codex executing on its side.
                            // Accept `call_id` primarily, but fall back to `id`
                            // — some clients send the Responses-API-style `id`
                            // field here (repro variant D of the user
                            // synthetic-call-id bug) and dropping it forced the
                            // connector to synthesize a mismatched id.
                            var callId = item.TryGetProperty("call_id", out var cidEl) ? cidEl.GetString() : null;
                            if (string.IsNullOrEmpty(callId))
                                callId = item.TryGetProperty("id", out var idFallbackEl) ? idFallbackEl.GetString() : null;
                            var output = item.TryGetProperty("output", out var outEl) ? outEl.GetString() : "";
                            messages.Add(new
                            {
                                role = "tool",
                                content = output,
                                tool_call_id = callId
                            });
                        }
                        else if (type == "function_call")
                        {
                            // A STANDALONE function_call item in the input: Codex emits the
                            // model's tool call(s) as sibling item(s) on the tool-result turn,
                            // NOT nested inside an assistant message content array. Each call
                            // must be represented by an assistant message whose tool_calls[]
                            // the following function_call_output tool message(s) reference.
                            //
                            // PARALLEL CALLS: when the model issues several tool calls in one
                            // turn, Codex sends them as CONTIGUOUS function_call items followed
                            // by their CONTIGUOUS function_call_output items. They MUST collapse
                            // into a SINGLE assistant message carrying all tool_calls, then the
                            // tool messages — otherwise we emit
                            //   assistant[call_A] -> assistant[call_B] -> tool[A] -> tool[B]
                            // and tool[B] lands directly after another tool message, which is
                            // INVALID and DeepSeek rejects with HTTP 400
                            // ("Console Go: Upstream request failed").
                            //
                            // Fix: only FLUSH the assistant tool_calls message when the NEXT
                            // item is NOT a function_call (i.e. we've reached the end of this
                            // call group). Accumulate consecutive calls into one assistant msg.
                            var fcId = (item.TryGetProperty("id", out var fidEl) ? fidEl.GetString() : null)
                                    ?? (item.TryGetProperty("call_id", out var fcidEl) ? fcidEl.GetString() : null);
                            var fcName = item.TryGetProperty("name", out var fnEl) ? fnEl.GetString() : "";
                            var fcArgs = item.TryGetProperty("arguments", out var faEl) ? faEl.GetString() : "{}";
                            pendingToolCalls.Add(new
                            {
                                id = fcId ?? $"call_{Guid.NewGuid():N}",
                                type = "function",
                                function = new { name = fcName, arguments = fcArgs }
                            });

                            // Look ahead: is the next item also a function_call?
                            var nextIdx = idx + 1;
                            var nextIsFunctionCall = nextIdx < inputElValue.Length &&
                                inputElValue[nextIdx].TryGetProperty("type", out var nt) &&
                                nt.GetString() == "function_call";
                            if (!nextIsFunctionCall)
                            {
                                // End of this call group → flush one assistant message.
                                var calls = pendingToolCalls.ToArray();
                                pendingToolCalls.Clear();
                                messages.Add(new
                                {
                                    role = "assistant",
                                    content = "",
                                    tool_calls = calls
                                });
                            }
                        }
                    }
                }
            }

            if (messages.Count == 0)
            {
                messages.Add(new { role = "user", content = "..." });
            }

            // Gemini must never fall through to generic OpenAI-compatible HTTP.
            if (geminiTarget is not null
                || resolvedProviderCode.Equals("gemini-subscription", StringComparison.OrdinalIgnoreCase))
            {
                var nativeRoute = geminiTarget?.RouteKind == ProviderRouteKind.NativeGemini;
                var geminiService = nativeRoute
                    ? chatServices.FirstOrDefault(x => x.ProviderName.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
                    : chatServices.FirstOrDefault(x => x.ProviderName.Equals("gemini-subscription", StringComparison.OrdinalIgnoreCase));
                if (geminiService is null)
                    return Results.Problem("Gemini connector is not available.", statusCode: 503);
                var geminiRequest = new Arkana.Domain.Interfaces.ChatRequest
                {
                    Model = model,
                    Messages = ToChatMessages(messages),
                    TenantId = tenantId,
                    PreferredProviderCode = nativeRoute
                        ? targetProvider?.Code ?? "gemini"
                        : geminiTarget?.AccountCode ?? preferredProviderCode,
                    PreferredProviderId = geminiTarget?.ProviderId ?? targetProvider?.Id,
                    PreferredProviderAccountId = geminiTarget?.ProviderAccountId ?? requestedAccountId,
                    PreferredProviderAccountCode = geminiTarget?.AccountCode,
                    AllowProviderFallback = allowProviderFallback,
                    Tools = body.Tools is { } geminiTools
                        ? JsonSerializer.Deserialize<List<Arkana.Domain.Interfaces.ToolDefinition>>(JsonSerializer.Serialize(TranslateTools(geminiTools), JsonOpts), JsonOpts)
                        : null,
                    ToolChoice = body.ToolChoice?.Clone()
                };
                if (isStream && geminiService is IStreamingChatCompletionService streamingGemini)
                    return await StreamGeminiResponsesAsync(streamingGemini, geminiRequest, httpContext,
                        tokenTracker, requestLogger, streamCounter, providerRepo, responseId, model,
                        messages, apiKeyName, resolvedProviderCode, sw);

                var geminiResult = await geminiService.CompleteAsync(geminiRequest, httpContext.RequestAborted);
                if (!geminiResult.IsSuccess)
                {
                    var status = geminiResult.UpstreamStatus ?? 502;
                    return Results.Json(new { type = "error", error = new { message = geminiResult.ErrorMessage ?? "Gemini request failed.", type = "upstream_error" } }, JsonOpts, statusCode: status);
                }
                var output = new List<object>();
                if (!string.IsNullOrEmpty(geminiResult.Content))
                    output.Add(new { type = "message", id = $"msg_{Guid.NewGuid():N}", role = "assistant", content = new[] { new { type = "output_text", text = geminiResult.Content, annotations = Array.Empty<string>() } } });
                if (geminiResult.ToolCalls is { Count: > 0 })
                    output.AddRange(geminiResult.ToolCalls.Select(tc => (object)new { type = "function_call", id = tc.Id, name = tc.FunctionName, arguments = tc.FunctionArguments, status = "completed" }));
                var terminal = new { type = "response.completed", response = new { id = responseId, @object = "response", created, model, status = "completed", output, usage = new { input_tokens = geminiResult.InputTokens, output_tokens = geminiResult.OutputTokens, total_tokens = geminiResult.InputTokens + geminiResult.OutputTokens } } };
                sw.Stop();
                var actualGeminiProviderCode = geminiResult.ResolvedProviderAccountCode
                    ?? resolvedProviderCode;
                var geminiProvider = await providerRepo.GetByCodeAsync(
                    actualGeminiProviderCode, tenantId, httpContext.RequestAborted)
                    ?? await providerRepo.GetByCodeAsync(resolvedProviderCode, tenantId, httpContext.RequestAborted);
                var geminiCost = (geminiResult.InputTokens * (geminiProvider?.CostPerInputToken ?? 0m))
                    + (geminiResult.OutputTokens * (geminiProvider?.CostPerOutputToken ?? 0m));
                await tokenTracker.RecordUsageAsync(new TokenUsage(actualGeminiProviderCode, geminiResult.Model,
                    geminiResult.InputTokens, geminiResult.OutputTokens, geminiCost, sw.Elapsed, apiKeyName));
                await requestLogger.TryRecordWithContextAsync(new RequestLog
                {
                    Provider = actualGeminiProviderCode,
                    Model = geminiResult.Model,
                    ApiKeyName = apiKeyName,
                    RequestedProviderCode = preferredProviderCode,
                    RouteKind = string.IsNullOrWhiteSpace(geminiResult.RouteKind) || geminiResult.RouteKind == "unknown"
                        ? (nativeRoute ? "native" : "broker-managed")
                        : geminiResult.RouteKind,
                    ResolvedProviderAccountId = geminiResult.ResolvedProviderAccountId,
                    ResolvedProviderAccountCode = geminiResult.ResolvedProviderAccountCode,
                    Messages = messages.Select(SerializeMessage).ToList(),
                    ResponseContent = geminiResult.Content,
                    InputTokens = geminiResult.InputTokens,
                    OutputTokens = geminiResult.OutputTokens,
                    Cost = geminiCost,
                    Duration = sw.Elapsed,
                    Timestamp = DateTimeOffset.UtcNow
                }, httpContext);
                return Results.Json(terminal, JsonOpts);
            }

            // ChatGPT/Codex providers use the gateway's OAuth-backed connector,
            // not a generic upstream HTTP provider. Reuse the existing connector
            // and translate its chat-completions SSE into Responses SSE.
            if (isStream && resolvedProviderCode.StartsWith("chatgpt", StringComparison.OrdinalIgnoreCase))
            {
                var chatGpt = chatServices.OfType<Arkana.Infrastructure.AI.ChatGptCodexChatService>().FirstOrDefault();
                if (chatGpt is null)
                    return Results.Problem("ChatGPT connector is not available.", statusCode: 503);

                // BUG FIX (2026-08-24, synthetic-call-id): the old code
                // round-tripped the wire-shaped messages through
                //   Deserialize<List<ChatMessage>>(Serialize(messages))
                // with a CamelCase-only policy. The anonymous objects use
                // snake_case keys (`tool_call_id`, `tool_calls`) which never
                // matched the deserializer's expected property names
                // (`toolCallId`, `toolCalls`) — underscores are not case, so
                // PropertyNameCaseInsensitive wouldn't help either. Result:
                // ToolCallId was ALWAYS null while the assistant function_call
                // items replayed their REAL ids, so BuildResponsesInput paired
                // real assistant ids with call_synthetic_* output ids and the
                // upstream rejected the request with HTTP 400
                // ("No tool call found for function call output with call_id
                // call_synthetic_*"). Map the fields explicitly instead.
                var chatMessages = ToChatMessages(messages);
                List<Arkana.Domain.Interfaces.ToolDefinition>? chatTools = null;
                if (body.Tools is { } responseTools)
                {
                    chatTools = JsonSerializer.Deserialize<List<Arkana.Domain.Interfaces.ToolDefinition>>(
                        JsonSerializer.Serialize(TranslateTools(responseTools), JsonOpts), JsonOpts);
                }

                var chatRequest = new Arkana.Domain.Interfaces.ChatRequest
                {
                    Model = model,
                    Messages = chatMessages,
                    TenantId = tenantId,
                    Tools = chatTools,
                    ToolChoice = body.ToolChoice?.Clone(),
                    PreferredProviderCode = preferredProviderCode,
                    AllowProviderFallback = allowProviderFallback
                };
                var (chatStream, _, chatError) = await chatGpt.CompleteStreamingAsync(
                    chatRequest, httpContext.RequestAborted);
                if (chatStream is null)
                    return Results.Problem(chatError ?? "ChatGPT streaming failed.", statusCode: 502);

                httpContext.Response.ContentType = "text/event-stream";
                httpContext.Response.Headers.CacheControl = "no-cache";
                httpContext.Response.Headers.Connection = "keep-alive";
                httpContext.Response.Headers["X-Accel-Buffering"] = "no";
                var chatUsage = await TranslateChatCompletionsToResponsesAsync(
                    chatStream, httpContext, responseId, false, model, httpContext.RequestAborted);
                sw.Stop();
                var actualProviderCode = chatGpt.LastSelectedAccountCode
                    ?? preferredProviderCode
                    ?? resolvedProviderCode;
                var actualProvider = await providerRepo.GetByCodeAsync(
                    actualProviderCode, tenantId, httpContext.RequestAborted);
                var actualInputRate = actualProvider?.CostPerInputToken ?? 0m;
                var actualOutputRate = actualProvider?.CostPerOutputToken ?? 0m;
                var chatCost = (chatUsage.PromptTokens * actualInputRate) +
                    (chatUsage.CompletionTokens * actualOutputRate);
                await tokenTracker.RecordUsageAsync(new TokenUsage(
                    actualProviderCode, model, chatUsage.PromptTokens, chatUsage.CompletionTokens,
                    chatCost, sw.Elapsed, apiKeyName));
                await requestLogger.TryRecordWithContextAsync(new RequestLog
                {
                    Provider = actualProviderCode,
                    Model = model,
                    ApiKeyName = apiKeyName,
                    Messages = messages.Select(SerializeMessage).ToList(),
                    ResponseContent = null,
                    InputTokens = chatUsage.PromptTokens,
                    OutputTokens = chatUsage.CompletionTokens,
                    Cost = chatCost,
                    Duration = sw.Elapsed,
                    Timestamp = DateTimeOffset.UtcNow
                }, httpContext);
                return Results.Empty;
            }

            // ── Streaming mode ───────────────────────────────────
            if (isStream)
            {
                httpContext.Response.ContentType = "text/event-stream";
                httpContext.Response.Headers.CacheControl = "no-cache";
                httpContext.Response.Headers.Connection = "keep-alive";
                httpContext.Response.Headers["X-Accel-Buffering"] = "no";
                httpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
                // SSE heartbeats keep strict clients (Codex) alive during upstream
                // reasoning/thinking delays where no token is emitted for many seconds.
                var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
                var heartbeatTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!heartbeatCts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(10), heartbeatCts.Token);
                            if (heartbeatCts.Token.IsCancellationRequested) break;
                            await httpContext.Response.WriteAsync(": heartbeat\n\n", heartbeatCts.Token);
                            await httpContext.Response.Body.FlushAsync(heartbeatCts.Token);
                        }
                    }
                    catch { /* client gone */ }
                }, heartbeatCts.Token);
                streamCounter.Increment();

                int promptTokens = 0, completionTokens = 0;
                string? errorMessage = null;
                var fullText = new System.Text.StringBuilder();
                // Remembers the terminal finish_reason seen from the upstream
                // translation ("stop" / "tool_calls" / "length"). "length" means
                // the generation was cut by max_output_tokens and the client
                // must see an incomplete response, not a completed one.
                string? lastFinishReason = null;

                // Track tool calls across streaming chunks
                var toolCallAccumulators = new Dictionary<int, (string id, string name, StringBuilder args, bool started)>();
                var toolCallIndices = new List<int>(); // preserve insertion order

                try
                {
                    // See ChatEndpoints: the SSRF-guarded client cannot
                    // reach a self-hosted plain-HTTP upstream.
                    var client = httpClientFactory.CreateClient(
                        upstreamBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                            ? "opencode-streaming"
                            : "local-streaming");
                    client.BaseAddress = new Uri(upstreamBaseUrl.TrimEnd('/') + "/");
                    if (!string.IsNullOrEmpty(upstreamApiKey))
                        client.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", upstreamApiKey);

                    // Build upstream body
                    var upstreamBodyDict = new Dictionary<string, object?>
                    {
                        ["model"] = model,
                        ["messages"] = messages,
                        ["stream"] = true
                    };
                    if (body.MaxOutputTokens.HasValue)
                        upstreamBodyDict["max_tokens"] = body.MaxOutputTokens.Value;

                    // Forward tools if present (responses → chat completions format)
                    if (body.Tools is not null)
                    {
                        upstreamBodyDict["tools"] = TranslateTools(body.Tools.Value);
                    }
                    if (body.ToolChoice is not null)
                    {
                        var tc = body.ToolChoice.Value;
                        if (tc.ValueKind == JsonValueKind.String)
                            upstreamBodyDict["tool_choice"] = tc.GetString();
                        else if (tc.TryGetProperty("type", out var tcType) && tcType.GetString() == "function" &&
                                 tc.TryGetProperty("name", out var tcName))
                        {
                            // Responses API: {type:"function", name:"Bash"}
                            // Chat Completions: {type:"function", function:{name:"Bash"}}
                            upstreamBodyDict["tool_choice"] = new Dictionary<string, object?>
                            {
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?> { ["name"] = tcName.GetString() }
                            };
                        }
                        else
                        {
                            upstreamBodyDict["tool_choice"] = tc.GetRawText();
                        }
                    }

                    var upstreamJson = JsonSerializer.Serialize(upstreamBodyDict, JsonOpts);

                    var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
                    {
                        Content = new StringContent(upstreamJson)
                    };
                    upstreamRequest.Content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    upstreamRequest.Headers.Accept.Add(
                        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                    // Emit response.created event first
                    var createdEvent = JsonSerializer.Serialize(new
                    {
                        type = "response.created",
                        response = new
                        {
                            id = responseId,
                            @object = "response",
                            created,
                            model
                        }
                    }, JsonOpts);
                    await httpContext.Response.WriteAsync($"event: response.created\ndata: {createdEvent}\n\n", httpContext.RequestAborted);
                    await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);

                    // Emit response.in_progress
                    var inProgressEvent = JsonSerializer.Serialize(new
                    {
                        type = "response.in_progress",
                        response = new { id = responseId }
                    }, JsonOpts);
                    await httpContext.Response.WriteAsync($"event: response.in_progress\ndata: {inProgressEvent}\n\n", httpContext.RequestAborted);

                    var msgId = $"msg_{Guid.NewGuid():N}";
                    bool hasTextOutput = false;

                    // NOT 'using var': the retry path below reassigns this variable.
                    var upstreamResponse = await client.SendAsync(upstreamRequest,
                        HttpCompletionOption.ResponseHeadersRead, httpContext.RequestAborted);

                    if (!upstreamResponse.IsSuccessStatusCode)
                    {
                        var errorBody = await upstreamResponse.Content.ReadAsStringAsync(httpContext.RequestAborted);
                        logger.LogWarning("[Responses API] Upstream error status={StatusCode}", (int)upstreamResponse.StatusCode);
                        // Model-unsupported upstream errors (e.g. "Model gpt-4o is not supported")
                        // would otherwise sever the SSE stream mid-turn (Codex "Reconnecting... 5/5").
                        // Retry once with a known-good fallback model so the chat survives.
                        bool isModelError = (int)upstreamResponse.StatusCode is 400 or 401
                            && errorBody.Contains("not supported", StringComparison.OrdinalIgnoreCase);
                        var fallbackModel = Configuration.GatewayDefaults.DefaultModel;
                        if (isModelError && model != fallbackModel
                            && !ApiKeyModelAuthorization.IsRestricted(httpContext))
                        {
                            logger.LogWarning(
                                "[Responses API] Model '{Model}' rejected upstream; retrying with '{Fallback}'.",
                                model, fallbackModel);
                            model = fallbackModel;
                            if (upstreamBodyDict.ContainsKey("model"))
                                upstreamBodyDict["model"] = model;
                            else
                                upstreamBodyDict.Add("model", model);
                            var retryReq = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
                            {
                                Content = new StringContent(JsonSerializer.Serialize(upstreamBodyDict, JsonOpts))
                            };
                            retryReq.Content.Headers.ContentType =
                                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                            retryReq.Headers.Accept.Add(
                                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                            if (!string.IsNullOrEmpty(upstreamApiKey))
                                retryReq.Headers.Authorization =
                                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", upstreamApiKey);
                            upstreamResponse = await client.SendAsync(retryReq,
                                HttpCompletionOption.ResponseHeadersRead, httpContext.RequestAborted);
                            if (upstreamResponse.IsSuccessStatusCode)
                                goto streamFromUpstream; // success on retry -> stream normally
                            errorBody = await upstreamResponse.Content.ReadAsStringAsync(httpContext.RequestAborted);
                            logger.LogWarning("[Responses API] Upstream retry failed status={StatusCode}", (int)upstreamResponse.StatusCode);
                        }
                        // Emit a clean terminal event instead of throwing into a severed stream.
                        var failedEvent = JsonSerializer.Serialize(new
                        {
                            type = "response.failed",
                            response = new
                            {
                                id = responseId,
                                status = "failed",
                                error = new
                                {
                                    message = SanitizedUpstreamError((int)upstreamResponse.StatusCode, resolvedProviderCode),
                                    type = "gateway_error"
                                }
                            }
                        }, JsonOpts);
                        await httpContext.Response.WriteAsync($"data: {failedEvent}\n\n", httpContext.RequestAborted);
                        await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
                        heartbeatCts.Cancel();
                        await heartbeatTask;
                        heartbeatCts.Dispose();
                        return Results.Empty;
                    }
                    upstreamResponse.EnsureSuccessStatusCode();

                    streamFromUpstream:
                    using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(httpContext.RequestAborted);
                    using var reader = new StreamReader(upstreamStream);

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
                                    var root = doc.RootElement;

                                    // Extract usage for final tally
                                    if (root.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
                                    {
                                        if (usageElem.TryGetProperty("prompt_tokens", out var pt))
                                            promptTokens = pt.GetInt32();
                                        if (usageElem.TryGetProperty("completion_tokens", out var ct))
                                            completionTokens = ct.GetInt32();
                                    }

                                    if (root.TryGetProperty("choices", out var choices) &&
                                        choices.ValueKind == JsonValueKind.Array &&
                                        choices.GetArrayLength() > 0)
                                    {
                                        JsonElement? delta = choices[0].TryGetProperty("delta", out var d) ? d : null;
                                        var finishReason = choices[0].TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
                                        if (!string.IsNullOrEmpty(finishReason))
                                            lastFinishReason = finishReason;

                                        if (delta is not null && delta.Value.ValueKind == JsonValueKind.Object)
                                        {
                                            // ── Handle text content delta ──
                                            if (delta.Value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                                            {
                                                var text = content.GetString() ?? "";
                                                if (text.Length > 0)
                                                {
                                                    if (!hasTextOutput)
                                                    {
                                                        hasTextOutput = true;
                                                        // Emit response.output_item.added for text
                                                        var itemAdded = JsonSerializer.Serialize(new
                                                        {
                                                            type = "response.output_item.added",
                                                            output_index = 0,
                                                            item = new
                                                            {
                                                                id = msgId,
                                                                type = "message",
                                                                role = "assistant",
                                                                content = new[] { new { type = "output_text", text = "", annotations = Array.Empty<string>() } }
                                                            }
                                                        }, JsonOpts);
                                                        await httpContext.Response.WriteAsync($"event: response.output_item.added\ndata: {itemAdded}\n\n", httpContext.RequestAborted);

                                                        var partAdded = JsonSerializer.Serialize(new
                                                        {
                                                            type = "response.content_part.added",
                                                            index = 0,
                                                            part = new { type = "output_text", text = "" }
                                                        }, JsonOpts);
                                                        await httpContext.Response.WriteAsync($"event: response.content_part.added\ndata: {partAdded}\n\n", httpContext.RequestAborted);
                                                        await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
                                                    }

                                                    fullText.Append(text);
                                                    collectedContent.Append(text);

                                                    var deltaEvent = JsonSerializer.Serialize(new
                                                    {
                                                        type = "response.output_text.delta",
                                                        delta = text,
                                                        index = 0
                                                    }, JsonOpts);
                                                    await httpContext.Response.WriteAsync(
                                                        $"event: response.output_text.delta\ndata: {deltaEvent}\n\n",
                                                        httpContext.RequestAborted);
                                                    await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
                                                }
                                            }

                                            // ── Handle tool_calls delta ──
                                            if (delta.Value.TryGetProperty("tool_calls", out var toolCalls) &&
                                                toolCalls.ValueKind == JsonValueKind.Array)
                                            {
                                                foreach (var tc in toolCalls.EnumerateArray())
                                                {
                                                    var idx = tc.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : 0;
                                                    var hasId = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String;
                                                    
                                                    // Extract function name and args via explicit checks to avoid CS0165
                                                    tc.TryGetProperty("function", out var fnEl);
                                                    string? tcFnName = null;
                                                    string? tcFnArgs = null;
                                                    bool hasFnName = false, hasArgs = false;
                                                    if (fnEl.ValueKind == JsonValueKind.Object)
                                                    {
                                                        if (fnEl.TryGetProperty("name", out var fnNameEl) && fnNameEl.ValueKind == JsonValueKind.String)
                                                        {
                                                            hasFnName = true;
                                                            tcFnName = fnNameEl.GetString();
                                                        }
                                                        if (fnEl.TryGetProperty("arguments", out var fnArgsEl) && fnArgsEl.ValueKind == JsonValueKind.String)
                                                        {
                                                            hasArgs = true;
                                                            tcFnArgs = fnArgsEl.GetString();
                                                        }
                                                    }

                                                    if (!toolCallAccumulators.TryGetValue(idx, out var existingTc))
                                                    {
                                                        // New tool call
                                                        var callId = hasId ? idEl.GetString()! : $"call_{Guid.NewGuid():N}";
                                                        var fnName = hasFnName ? tcFnName! : "unknown";

                                                        toolCallAccumulators[idx] = (callId, fnName, new StringBuilder(), false);
                                                        toolCallIndices.Add(idx);

                                                        // Emit output_item.added for this function_call
                                                        var fcItemAdded = JsonSerializer.Serialize(new
                                                        {
                                                            type = "response.output_item.added",
                                                            output_index = idx + (hasTextOutput ? 1 : 0),
                                                            item = new
                                                            {
                                                                id = callId,
                                                                type = "function_call",
                                                                name = fnName,
                                                                arguments = "",
                                                                status = "in_progress"
                                                            }
                                                        }, JsonOpts);
                                                        await httpContext.Response.WriteAsync(
                                                            $"event: response.output_item.added\ndata: {fcItemAdded}\n\n",
                                                            httpContext.RequestAborted);
                                                    }

                                                    // Append arguments (for both new and continuing tool calls)
                                                    if (hasArgs)
                                                    {
                                                        var partialArgs = tcFnArgs!;
                                                        var (callId, fnName, argsSb, _) = toolCallAccumulators[idx];
                                                        argsSb.Append(partialArgs);

                                                        var fcDeltaEvent = JsonSerializer.Serialize(new
                                                        {
                                                            type = "response.function_call_arguments.delta",
                                                            delta = partialArgs,
                                                            item_id = callId,
                                                            index = idx
                                                        }, JsonOpts);
                                                        await httpContext.Response.WriteAsync(
                                                            $"event: response.function_call_arguments.delta\ndata: {fcDeltaEvent}\n\n",
                                                            httpContext.RequestAborted);
                                                        await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
                                                    }
                                                }
                                            }
                                        }

                                        // ── Handle finish_reason = tool_calls ──
                                        // When streaming ends with tool_calls, we need to emit done events
                                        if (finishReason == "tool_calls" && toolCallAccumulators.Count > 0)
                                        {
                                            // Tool call done events will be emitted after the loop
                                        }
                                    }
                                }
                                catch { /* best-effort parse */ }
                            }
                        }
                    }

                    sw.Stop();

                    var noCancel = CancellationToken.None;

                    // ── Emit done events for text output ──
                    if (hasTextOutput)
                    {
                        var doneTextEvent = JsonSerializer.Serialize(new
                        {
                            type = "response.output_text.done",
                            text = fullText.ToString(),
                            index = 0
                        }, JsonOpts);
                        await httpContext.Response.WriteAsync(
                            $"event: response.output_text.done\ndata: {doneTextEvent}\n\n", noCancel);
                        await httpContext.Response.Body.FlushAsync(noCancel);

                        var partDoneEvent = JsonSerializer.Serialize(new
                        {
                            type = "response.content_part.done",
                            index = 0,
                            part = new { type = "output_text", text = fullText.ToString() }
                        }, JsonOpts);
                        await httpContext.Response.WriteAsync(
                            $"event: response.content_part.done\ndata: {partDoneEvent}\n\n", noCancel);

                        var itemDoneEvent = JsonSerializer.Serialize(new
                        {
                            type = "response.output_item.done",
                            output_index = 0,
                            item = new
                            {
                                id = msgId,
                                type = "message",
                                role = "assistant",
                                content = new[]
                                {
                                    new { type = "output_text", text = fullText.ToString(), annotations = Array.Empty<string>() }
                                }
                            }
                        }, JsonOpts);
                        await httpContext.Response.WriteAsync(
                            $"event: response.output_item.done\ndata: {itemDoneEvent}\n\n", noCancel);
                    }

                    // ── Emit done events for each tool call ──
                    foreach (var idx in toolCallIndices)
                    {
                        var (callId, fnName, argsSb, _) = toolCallAccumulators[idx];
                        var finalArgs = argsSb.ToString();

                        // function_call_arguments.done
                        var fcDoneEvent = JsonSerializer.Serialize(new
                        {
                            type = "response.function_call_arguments.done",
                            item_id = callId,
                            index = idx,
                            name = fnName,
                            arguments = finalArgs
                        }, JsonOpts);
                        await httpContext.Response.WriteAsync(
                            $"event: response.function_call_arguments.done\ndata: {fcDoneEvent}\n\n", noCancel);

                        // output_item.done
                        var fcItemDone = JsonSerializer.Serialize(new
                        {
                            type = "response.output_item.done",
                            output_index = idx + (hasTextOutput ? 1 : 0),
                            item = new
                            {
                                id = callId,
                                type = "function_call",
                                name = fnName,
                                arguments = finalArgs,
                                status = "completed"
                            }
                        }, JsonOpts);
                        await httpContext.Response.WriteAsync(
                            $"event: response.output_item.done\ndata: {fcItemDone}\n\n", noCancel);
                    }

                    // ── Build output array for response.completed ──
                    var outputList = new List<object>();

                    if (hasTextOutput)
                    {
                        outputList.Add(new
                        {
                            type = "message",
                            id = msgId,
                            role = "assistant",
                            content = new[]
                            {
                                new { type = "output_text", text = fullText.ToString(), annotations = Array.Empty<string>() }
                            }
                        });
                    }

                    foreach (var idx in toolCallIndices)
                    {
                        var (callId, fnName, argsSb, _) = toolCallAccumulators[idx];
                        outputList.Add(new
                        {
                            type = "function_call",
                            id = callId,
                            name = fnName,
                            arguments = argsSb.ToString(),
                            status = "completed"
                        });
                    }

                    // Emit response.completed (or response.incomplete when the
                    // upstream turn was cut by max_output_tokens — finish_reason
                    // "length" from the chat-completions translation layer).
                    var upstreamTruncated = lastFinishReason == "length";
                    var upstreamProviderBlocked = lastFinishReason is null
                        || (lastFinishReason != "stop" && lastFinishReason != "length");
                    var completedEvent = JsonSerializer.Serialize(new
                    {
                        type = upstreamProviderBlocked ? "response.failed" : upstreamTruncated ? "response.incomplete" : "response.completed",
                        response = new
                        {
                            id = responseId,
                            @object = "response",
                            created,
                            model,
                            status = upstreamProviderBlocked ? "failed" : upstreamTruncated ? "incomplete" : "completed",
                            output = outputList,
                            usage = new
                            {
                                input_tokens = promptTokens,
                                output_tokens = completionTokens,
                                total_tokens = promptTokens + completionTokens
                            }
                        }
                    }, JsonOpts);
                    await httpContext.Response.WriteAsync(
                        $"event: {(upstreamProviderBlocked ? "response.failed" : upstreamTruncated ? "response.incomplete" : "response.completed")}\ndata: {completedEvent}\n\n", noCancel);
                    await httpContext.Response.Body.FlushAsync(noCancel);

                    heartbeatCts.Cancel();
                    try { await heartbeatTask; } catch { /* cancelled */ }
                    heartbeatCts.Dispose();

                    if (httpContext.Response.HasStarted)
                    {
                        await httpContext.Response.CompleteAsync();
                    }

                    // ── Log ──
                    var costS = (promptTokens * resolvedCostPerInput) + (completionTokens * resolvedCostPerOutput);
                    var usageS = new TokenUsage(resolvedProviderCode, model, promptTokens, completionTokens, costS, sw.Elapsed, apiKeyName);
                    await tokenTracker.RecordUsageAsync(usageS);

                    var logMessages = messages.Select(SerializeMessage).ToList();
                    var requestLog = new RequestLog
                    {
                        Provider = resolvedProviderCode,
                        Model = model,
                        ApiKeyName = apiKeyName,
                        Messages = logMessages,
                        ResponseContent = collectedContent.Length > 0 ? collectedContent.ToString() : null,
                        InputTokens = promptTokens,
                        OutputTokens = completionTokens,
                        Cost = costS,
                        Duration = sw.Elapsed,
                        Timestamp = DateTimeOffset.UtcNow
                    };
                    await requestLogger.TryRecordWithContextAsync(requestLog, httpContext);
                }
                catch (OperationCanceledException)
                {
                    sw.Stop();
                    errorMessage = "Client disconnected";
                    heartbeatCts.Cancel();
                    try { await heartbeatTask; } catch { /* cancelled */ }
                    heartbeatCts.Dispose();
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    errorMessage = "Client disconnected";
                    heartbeatCts.Cancel();
                    try { await heartbeatTask; } catch { /* cancelled */ }
                    heartbeatCts.Dispose();
                    logger.LogError("[Responses API] Error type={ExceptionType}", ex.GetType().Name);

                    var errEvent = JsonSerializer.Serialize(new
                    {
                        type = "error",
                        error = new { message = "Gemini upstream request failed unexpectedly.", type = "gateway_error" }
                    }, JsonOpts);
                    await httpContext.Response.WriteAsync($"data: {errEvent}\n\n", CancellationToken.None);
                    await httpContext.Response.Body.FlushAsync(CancellationToken.None);

                    var logMessages = messages.Select(SerializeMessage).ToList();
                    var errLog = new RequestLog
                    {
                        Provider = resolvedProviderCode,
                        Model = model,
                        ApiKeyName = apiKeyName,
                        Messages = logMessages,
                        ResponseContent = collectedContent.Length > 0 ? collectedContent.ToString() : null,
                        InputTokens = 0,
                        OutputTokens = 0,
                        Cost = 0,
                        Duration = sw.Elapsed,
                        Timestamp = DateTimeOffset.UtcNow,
                        IsError = true,
                        ErrorMessage = errorMessage
                    };
                    await requestLogger.TryRecordWithContextAsync(errLog, httpContext);
                }
                finally
                {
                    streamCounter.Decrement();
                }

                return Results.Empty;
            }

            // ── Non-streaming mode ───────────────────────────────
            try
            {
                // Use the injected shared IHttpClientFactory (PERF-ARKANA-005) —
                // NOT `new HttpClient()` here, which leaked a socket per request
                // and would exhaust the connection pool under load.
                var client = httpClientFactory.CreateClient();
                client.BaseAddress = new Uri(upstreamBaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromMinutes(5);
                if (!string.IsNullOrEmpty(upstreamApiKey))
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", upstreamApiKey);

                var upstreamBodyDict = new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["messages"] = messages,
                    ["stream"] = false
                };
                if (body.MaxOutputTokens.HasValue)
                    upstreamBodyDict["max_tokens"] = body.MaxOutputTokens.Value;

                // Forward tools if present
                if (body.Tools is not null)
                {
                    upstreamBodyDict["tools"] = TranslateTools(body.Tools.Value);
                }
                if (body.ToolChoice is not null)
                {
                    var tc = body.ToolChoice.Value;
                    if (tc.ValueKind == JsonValueKind.String)
                        upstreamBodyDict["tool_choice"] = tc.GetString();
                    else if (tc.TryGetProperty("type", out var tcType) && tcType.GetString() == "function" &&
                             tc.TryGetProperty("name", out var tcName))
                    {
                        upstreamBodyDict["tool_choice"] = new Dictionary<string, object?>
                        {
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object?> { ["name"] = tcName.GetString() }
                        };
                    }
                    else
                    {
                        upstreamBodyDict["tool_choice"] = tc.GetRawText();
                    }
                }

                var upstreamJson = JsonSerializer.Serialize(upstreamBodyDict, JsonOpts);

                var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
                {
                    Content = new StringContent(upstreamJson)
                };
                upstreamRequest.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                using var upstreamResponse = await client.SendAsync(upstreamRequest, httpContext.RequestAborted);
                upstreamResponse.EnsureSuccessStatusCode();

                var responseJson = await upstreamResponse.Content.ReadAsStringAsync(httpContext.RequestAborted);
                using var responseDoc = JsonDocument.Parse(responseJson);
                var respRoot = responseDoc.RootElement;

                string content = "";
                List<object>? toolCalls = null;
                int inTokens = 0, outTokens = 0;

                if (respRoot.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (!choice.TryGetProperty("finish_reason", out var finishReasonElement)
                        || finishReasonElement.ValueKind != JsonValueKind.String)
                    {
                        httpContext.Response.StatusCode = 502;
                        return Results.Json(new { type = "error", error = new { message = "Gemini response blocked by provider policy.", type = "upstream_error" } }, JsonOpts);
                    }
                    var finishReason = finishReasonElement.GetString();
                    if (finishReason is not "stop" and not "length")
                    {
                        httpContext.Response.StatusCode = 502;
                        return Results.Json(new { type = "error", error = new { message = "Gemini response blocked by provider policy.", type = "upstream_error" } }, JsonOpts);
                    }
                    if (choice.TryGetProperty("message", out var msg))
                    {
                        if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                            content = c.GetString() ?? "";

                        // Extract tool_calls from the message
                        if (msg.TryGetProperty("tool_calls", out var tcArray) && tcArray.ValueKind == JsonValueKind.Array)
                        {
                            toolCalls = new List<object>();
                            foreach (var tc in tcArray.EnumerateArray())
                            {
                                var callId = tc.TryGetProperty("id", out var cidEl) ? cidEl.GetString() : $"call_{Guid.NewGuid():N}";
                                string? fnName = null;
                                string? fnArgs = null;
                                if (tc.TryGetProperty("function", out var fnEl))
                                {
                                    if (fnEl.TryGetProperty("name", out var fnNameEl))
                                        fnName = fnNameEl.GetString();
                                    if (fnEl.TryGetProperty("arguments", out var fnArgsEl))
                                        fnArgs = fnArgsEl.GetString();
                                }
                                toolCalls.Add(new
                                {
                                    type = "function_call",
                                    id = callId,
                                    name = fnName ?? "unknown",
                                    arguments = fnArgs ?? "{}",
                                    status = "completed"
                                });
                            }
                        }
                    }
                }

                if (respRoot.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("prompt_tokens", out var pt)) inTokens = pt.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var ct)) outTokens = ct.GetInt32();
                }

                if (!string.IsNullOrEmpty(content))
                    collectedContent.Append(content);
                sw.Stop();

                var costN = (inTokens * resolvedCostPerInput) + (outTokens * resolvedCostPerOutput);
                var usageN = new TokenUsage(resolvedProviderCode, model, inTokens, outTokens, costN, sw.Elapsed, apiKeyName);
                await tokenTracker.RecordUsageAsync(usageN);

                var logMsgs = messages.Select(SerializeMessage).ToList();
                var reqLog = new RequestLog
                {
                    Provider = resolvedProviderCode,
                    Model = model,
                    ApiKeyName = apiKeyName,
                    Messages = logMsgs,
                    ResponseContent = content,
                    InputTokens = inTokens,
                    OutputTokens = outTokens,
                    Cost = costN,
                    Duration = sw.Elapsed,
                    Timestamp = DateTimeOffset.UtcNow
                };
                await requestLogger.TryRecordWithContextAsync(reqLog, httpContext);

                var finalResponse = BuildResponseObject(responseId, created, model, content, 
                    toolCalls, inTokens, outTokens);
                return Results.Json(finalResponse, JsonOpts);
            }
            catch (HttpRequestException ex)
            {
                sw.Stop();
                var statusCode = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : 502;
                httpContext.Response.StatusCode = statusCode;
                return Results.Json(new
                {
                    type = "error",
                    error = new
                    {
                        message = SanitizedUpstreamError(statusCode, resolvedProviderCode),
                        type = "upstream_error"
                    }
                }, JsonOpts);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                httpContext.Response.StatusCode = 504;
                return Results.Json(new
                {
                    type = "error",
                    error = new { message = "Gemini upstream request timed out.", type = "upstream_error" }
                }, JsonOpts);
            }
            catch (Exception)
            {
                sw.Stop();
                httpContext.Response.StatusCode = 502;
                return Results.Json(new
                {
                    type = "error",
                    error = new { message = "Gemini upstream request failed unexpectedly.", type = "upstream_error" }
                }, JsonOpts);
            }
        })
        .WithName("CreateResponse")
        .WithTags("Responses API")
        .WithOpenApi();
    }

    private static string SanitizedUpstreamError(int statusCode, string? providerCode)
    {
        var provider = providerCode?.ToLowerInvariant() switch
        {
            "opencode" => "OpenCode",
            "openrouter" => "OpenRouter",
            "ollama" => "Ollama",
            "chatgptcodex" => "ChatGPT",
            _ when providerCode?.StartsWith("chatgpt-", StringComparison.OrdinalIgnoreCase) == true => "ChatGPT",
            _ when providerCode?.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) == true => "Gemini",
            _ => "Upstream provider"
        };

        return statusCode switch
        {
            401 => $"{provider} authentication failed.",
            403 => $"{provider} authorization failed.",
            429 => $"{provider} rate limit exceeded.",
            >= 500 and <= 599 => $"{provider} service is unavailable.",
            _ => $"{provider} request failed (HTTP {statusCode}).",
        };
    }

    // ── Helper: Build the Responses API response object ──────────
    private static object BuildResponseObject(string responseId, long created, string model,
        string content, List<object>? toolCalls, int promptTokens, int completionTokens)
    {
        var outputList = new List<object>();

        // Text message output
        if (!string.IsNullOrEmpty(content))
        {
            outputList.Add(new
            {
                type = "message",
                id = $"msg_{Guid.NewGuid():N}",
                role = "assistant",
                content = new[]
                {
                    new
                    {
                        type = "output_text",
                        text = content,
                        annotations = Array.Empty<string>()
                    }
                }
            });
        }

        // Tool/function call outputs
        if (toolCalls is not null)
        {
            foreach (var tc in toolCalls)
            {
                outputList.Add(tc);
            }
        }

        return new
        {
            id = responseId,
            @object = "response",
            created,
            model,
            output = outputList,
            usage = new
            {
                input_tokens = promptTokens,
                output_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens
            }
        };
    }

    // ── Helper: Extract tool_calls from a Responses API input item ──
    private static object[]? ExtractToolCallsFromItem(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<object>();
        foreach (var part in contentEl.EnumerateArray())
        {
            if (part.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "function_call" &&
                part.TryGetProperty("id", out var callIdEl) &&
                part.TryGetProperty("name", out var nameEl) &&
                part.TryGetProperty("arguments", out var argsEl))
            {
                result.Add(new
                {
                    id = callIdEl.GetString(),
                    type = "function",
                    function = new
                    {
                        name = nameEl.GetString(),
                        arguments = argsEl.GetString()
                    }
                });
            }
        }

        return result.Count > 0 ? result.ToArray() : null;
    }

    private static async Task<IResult> StreamGeminiResponsesAsync(
        IStreamingChatCompletionService streamingGemini,
        Arkana.Domain.Interfaces.ChatRequest request,
        HttpContext httpContext,
        ITokenTracker tokenTracker,
        IRequestLogger requestLogger,
        ActiveStreamCounter streamCounter,
        IAiProviderRepository providerRepo,
        string responseId,
        string model,
        List<object> messages,
        string? apiKeyName,
        string resolvedProviderCode,
        System.Diagnostics.Stopwatch stopwatch)
    {
        var tenantId = httpContext.RequestServices.GetRequiredService<ITenantProvider>().TenantId
            ?? throw new InvalidOperationException("Authenticated tenant is required.");
        var streamResult = await streamingGemini.CompleteStreamingAsync(request, httpContext.RequestAborted);
        if (!streamResult.IsSuccess || streamResult.Stream is null)
        {
            var status = streamResult.Result.UpstreamStatus is >= 400 and <= 599
                ? streamResult.Result.UpstreamStatus
                : 503;
            await streamResult.DisposeAsync();
            return Results.Problem("Gemini streaming failed.", statusCode: status);
        }

        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";
        streamCounter.Increment();
        var terminalFrameStarted = false;

        try
        {
            var usage = await TranslateChatCompletionsToResponsesAsync(
                streamResult.Stream, httpContext, responseId,
                streamResult.Result.RouteKind.Equals("native", StringComparison.OrdinalIgnoreCase),
                streamResult.Result.Model ?? request.Model, httpContext.RequestAborted);
            terminalFrameStarted = usage.Completed;
            if (usage.Completed)
                streamResult.MarkCompleted();

            stopwatch.Stop();
            var actualProviderCode = streamResult.Result.ResolvedProviderAccountCode
                ?? resolvedProviderCode;
            var actualProvider = await providerRepo.GetByCodeAsync(actualProviderCode, tenantId, CancellationToken.None)
                ?? await providerRepo.GetByCodeAsync(resolvedProviderCode, tenantId, CancellationToken.None);
            var cost = (usage.PromptTokens * (actualProvider?.CostPerInputToken ?? 0m))
                + (usage.CompletionTokens * (actualProvider?.CostPerOutputToken ?? 0m));
            await tokenTracker.RecordUsageAsync(new TokenUsage(
                actualProviderCode, streamResult.Result.Model ?? model,
                usage.PromptTokens, usage.CompletionTokens, cost, stopwatch.Elapsed, apiKeyName));
            await requestLogger.TryRecordWithContextAsync(new RequestLog
            {
                Provider = actualProviderCode,
                Model = streamResult.Result.Model ?? model,
                ApiKeyName = apiKeyName,
                Messages = messages.Select(SerializeMessage).ToList(),
                InputTokens = usage.PromptTokens,
                OutputTokens = usage.CompletionTokens,
                Cost = cost,
                Duration = stopwatch.Elapsed,
                Timestamp = DateTimeOffset.UtcNow,
                RouteKind = streamResult.Result.RouteKind,
                ResolvedProviderAccountId = streamResult.Result.ResolvedProviderAccountId,
                ResolvedProviderAccountCode = streamResult.Result.ResolvedProviderAccountCode,
            }, httpContext);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is deliberately not marked as a completed response.
        }
        catch
        {
            if (!terminalFrameStarted)
            {
                terminalFrameStarted = true;
                try
                {
                    await httpContext.Response.WriteAsync(
                        "event: response.failed\ndata: {\"type\":\"response.failed\",\"error\":{\"message\":\"Gemini streaming failed.\",\"type\":\"gateway_error\"}}\n\n",
                        CancellationToken.None);
                    await httpContext.Response.Body.FlushAsync(CancellationToken.None);
                }
                catch
                {
                    // The client may have disconnected while the failure was being emitted.
                }
            }
        }
        finally
        {
            streamCounter.Decrement();
            await streamResult.DisposeAsync();
        }

        return Results.Empty;
    }

    private static async Task<(int PromptTokens, int CompletionTokens, bool Completed)> TranslateChatCompletionsToResponsesAsync(
        Stream chatStream, HttpContext httpContext, string responseId,
        bool nativeGemini, string model, CancellationToken ct)
    {
        using var reader = new StreamReader(chatStream, System.Text.Encoding.UTF8, true, 4096, true);
        var fullText = new StringBuilder();
        var textStarted = false;
        var messageId = $"msg_{Guid.NewGuid():N}";
        var created = JsonSerializer.Serialize(new
        {
            type = "response.created",
            response = new { id = responseId, @object = "response", status = "in_progress", output = Array.Empty<object>() }
        }, JsonOpts);
        await httpContext.Response.WriteAsync($"event: response.created\ndata: {created}\n\n", ct);
        var toolCalls = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
        var promptTokens = 0;
        var completionTokens = 0;
        var upstreamCompleted = false;
        var nativeState = new Arkana.Infrastructure.AI.GeminiNativeStreamState();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var data = line[6..];
            if (data == "[DONE]")
            {
                upstreamCompleted = true;
                break;
            }

            if (nativeGemini)
            {
                if (!Arkana.Infrastructure.AI.GeminiNativeStreamTranslator.TryTranslate(
                        data, model, nativeState, out var translated,
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

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
                throw new InvalidDataException("The provider returned an invalid streaming envelope.");
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                    promptTokens = pt.GetInt32();
                else if (usage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number)
                    promptTokens = it.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var ctEl) && ctEl.ValueKind == JsonValueKind.Number)
                    completionTokens = ctEl.GetInt32();
                else if (usage.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number)
                    completionTokens = ot.GetInt32();
            }
            if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object &&
                response.TryGetProperty("usage", out var responseUsage) && responseUsage.ValueKind == JsonValueKind.Object)
            {
                if (responseUsage.TryGetProperty("input_tokens", out var input) && input.ValueKind == JsonValueKind.Number)
                    promptTokens = input.GetInt32();
                if (responseUsage.TryGetProperty("output_tokens", out var outputTokenEl) && outputTokenEl.ValueKind == JsonValueKind.Number)
                    completionTokens = outputTokenEl.GetInt32();
            }
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
                continue;
            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                continue;

            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString() ?? string.Empty;
                if (text.Length == 0) continue;
                if (!textStarted)
                {
                    textStarted = true;
                    var added = JsonSerializer.Serialize(new
                    {
                        type = "response.output_item.added",
                        output_index = 0,
                        item = new { id = messageId, type = "message", role = "assistant", content = Array.Empty<object>() }
                    }, JsonOpts);
                    await httpContext.Response.WriteAsync($"event: response.output_item.added\ndata: {added}\n\n", ct);
                }
                fullText.Append(text);
                var deltaEvent = JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = text, output_index = 0, item_id = messageId, content_index = 0 }, JsonOpts);
                await httpContext.Response.WriteAsync($"event: response.output_text.delta\ndata: {deltaEvent}\n\n", ct);
            }

            if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    var index = call.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
                    var fn = call.TryGetProperty("function", out var fnEl) ? fnEl : default;
                    var name = fn.ValueKind == JsonValueKind.Object && fn.TryGetProperty("name", out var nameEl)
                        ? nameEl.GetString() ?? "unknown" : null;
                    var args = fn.ValueKind == JsonValueKind.Object && fn.TryGetProperty("arguments", out var argsEl)
                        ? argsEl.GetString() ?? string.Empty : string.Empty;
                    var id = call.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (!toolCalls.TryGetValue(index, out var state))
                    {
                        state = (id ?? $"call_{index}", name ?? "unknown", new StringBuilder());
                        toolCalls[index] = state;
                        var added = JsonSerializer.Serialize(new
                        {
                            type = "response.output_item.added",
                            output_index = index + (textStarted ? 1 : 0),
                            item = new { id = state.Id, call_id = state.Id, type = "function_call", name = state.Name, arguments = "", status = "in_progress" }
                        }, JsonOpts);
                        await httpContext.Response.WriteAsync($"event: response.output_item.added\ndata: {added}\n\n", ct);
                    }
                    if (args.Length > 0)
                    {
                        state.Args.Append(args);
                        var argEvent = JsonSerializer.Serialize(new { type = "response.function_call_arguments.delta", delta = args, item_id = state.Id, index }, JsonOpts);
                        await httpContext.Response.WriteAsync($"event: response.function_call_arguments.delta\ndata: {argEvent}\n\n", ct);
                    }
                }
            }
            await httpContext.Response.Body.FlushAsync(ct);
        }

        if (!upstreamCompleted)
            throw new InvalidDataException("The provider closed the stream before its terminal marker.");

        if (textStarted)
        {
            var textValue = fullText.ToString();
            var done = JsonSerializer.Serialize(new { type = "response.output_text.done", text = textValue, output_index = 0, item_id = messageId, content_index = 0 }, JsonOpts);
            await httpContext.Response.WriteAsync($"event: response.output_text.done\ndata: {done}\n\n", ct);
            var partDone = JsonSerializer.Serialize(new { type = "response.content_part.done", output_index = 0, content_index = 0, part = new { type = "output_text", text = textValue } }, JsonOpts);
            await httpContext.Response.WriteAsync($"event: response.content_part.done\ndata: {partDone}\n\n", ct);
            var itemDone = JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = 0, item = new { id = messageId, type = "message", role = "assistant", content = new[] { new { type = "output_text", text = textValue, annotations = Array.Empty<object>() } } } }, JsonOpts);
            await httpContext.Response.WriteAsync($"event: response.output_item.done\ndata: {itemDone}\n\n", ct);
        }
        foreach (var (index, state) in toolCalls)
        {
            var args = state.Args.ToString();
            var done = JsonSerializer.Serialize(new { type = "response.function_call_arguments.done", item_id = state.Id, index, name = state.Name, arguments = args }, JsonOpts);
            await httpContext.Response.WriteAsync($"event: response.function_call_arguments.done\ndata: {done}\n\n", ct);
            var itemDone = JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = index + (textStarted ? 1 : 0), item = new { id = state.Id, call_id = state.Id, type = "function_call", name = state.Name, arguments = args, status = "completed" } }, JsonOpts);
            await httpContext.Response.WriteAsync($"event: response.output_item.done\ndata: {itemDone}\n\n", ct);
        }
        var output = new List<object>();
        if (textStarted)
            output.Add(new { id = messageId, type = "message", role = "assistant", content = new[] { new { type = "output_text", text = fullText.ToString(), annotations = Array.Empty<object>() } } });
        output.AddRange(toolCalls.OrderBy(x => x.Key).Select(x => (object)new { id = x.Value.Id, call_id = x.Value.Id, type = "function_call", name = x.Value.Name, arguments = x.Value.Args.ToString(), status = "completed" }));
        var completed = JsonSerializer.Serialize(new
        {
            type = "response.completed",
            response = new
            {
                id = responseId,
                @object = "response",
                status = "completed",
                output,
                usage = new { input_tokens = promptTokens, output_tokens = completionTokens, total_tokens = promptTokens + completionTokens }
            }
        }, JsonOpts);
        await httpContext.Response.WriteAsync($"event: response.completed\ndata: {completed}\n\n", ct);
        await httpContext.Response.Body.FlushAsync(ct);
        return (promptTokens, completionTokens, true);
    }

    // ── Helper: Translate tools from Responses API format to Chat Completions format ──
    // Responses API: {"type":"function","name":"Bash","description":"...","parameters":{...}}
    // Chat Completions: {"type":"function","function":{"name":"Bash","description":"...","parameters":{...}}}
    private static List<object?> TranslateTools(JsonElement toolsEl)
    {
        var result = new List<object?>();
        if (toolsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in toolsEl.EnumerateArray())
            {
                var typeStr = tool.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

                // Only pass through "function" type tools — DeepSeek doesn't support
                // OpenAI-specific types like "namespace" or "computer_use"
                if (typeStr != "function")
                    continue;

                // Build the function wrapper
                var funcObj = new Dictionary<string, object?>();
                if (tool.TryGetProperty("name", out var nameEl))
                    funcObj["name"] = nameEl.GetString();
                if (tool.TryGetProperty("description", out var descEl))
                    funcObj["description"] = descEl.GetString();
                if (tool.TryGetProperty("parameters", out var paramsEl))
                    funcObj["parameters"] = JsonSerializer.Deserialize<object>(paramsEl.GetRawText(), JsonOpts);
                if (tool.TryGetProperty("strict", out var strictEl))
                    funcObj["strict"] = strictEl.GetBoolean();

                result.Add(new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = funcObj
                });
            }
        }
        return result;
    }

    // ── Helper: Map wire-shaped anonymous messages to typed ChatMessages ──
    // The chatgpt-streaming branch needs the REAL tool_call ids on tool-role
    // messages (and the assistant tool_calls list) to reach
    // ChatGptCodexChatService.BuildResponsesInput. This mapper reads both the
    // snake_case keys this endpoint builds (`tool_call_id`, `tool_calls`) and
    // the camelCase equivalents, so the mapping survives either shape. A
    // JSON round-trip with a CamelCase-only policy silently DROPPED these
    // fields (see the BUG FIX note at the call site).
    private static List<ChatMessage> ToChatMessages(List<object> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var msg in messages)
        {
            var json = JsonSerializer.Serialize(msg, JsonOpts);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string role = root.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";

            // Content may be a plain string; fall back to raw text for exotic shapes.
            string content = root.TryGetProperty("content", out var c)
                ? (c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.GetRawText())
                : "";

            string? toolCallId = null;
            if (root.TryGetProperty("tool_call_id", out var tcIdEl) && tcIdEl.ValueKind == JsonValueKind.String)
                toolCallId = tcIdEl.GetString();
            else if (root.TryGetProperty("toolCallId", out var tcIdCamelEl) && tcIdCamelEl.ValueKind == JsonValueKind.String)
                toolCallId = tcIdCamelEl.GetString();

            IReadOnlyList<ToolCall>? toolCalls = null;
            if ((root.TryGetProperty("tool_calls", out var tcsEl) || root.TryGetProperty("toolCalls", out tcsEl)) &&
                tcsEl.ValueKind == JsonValueKind.Array && tcsEl.GetArrayLength() > 0)
            {
                var calls = new List<ToolCall>();
                foreach (var call in tcsEl.EnumerateArray())
                {
                    calls.Add(new ToolCall
                    {
                        Id = (call.TryGetProperty("id", out var idEl) ? idEl.GetString() : null) ?? $"call_{Guid.NewGuid():N}",
                        Type = call.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "function" : "function",
                        Function = new ToolCallFunction
                        {
                            Name = call.TryGetProperty("function", out var fnEl) &&
                                   fnEl.TryGetProperty("name", out var nameEl)
                                ? nameEl.GetString() ?? ""
                                : "",
                            Arguments = call.TryGetProperty("function", out var fnArgsEl) &&
                                        fnArgsEl.TryGetProperty("arguments", out var argsEl)
                                ? argsEl.GetString() ?? ""
                                : "",
                        },
                    });
                }
                toolCalls = calls;
            }

            result.Add(new ChatMessage { Role = role, Content = content, ToolCallId = toolCallId, ToolCalls = toolCalls });
        }
        return result;
    }

    // ── Helper: Serialize a message object for logging ────────────
    private static ChatMessage SerializeMessage(object msg)
    {
        var json = JsonSerializer.Serialize(msg, JsonOpts);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new ChatMessage
        {
            Role = root.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user",
            Content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : ""
        };
    }

    // ── Helper: Extract text from Responses API content array ───
    private static string ExtractTextFromContent(JsonElement contentEl)
    {
        if (contentEl.ValueKind == JsonValueKind.String)
            return contentEl.GetString() ?? "";

        if (contentEl.ValueKind == JsonValueKind.Array)
        {
            // Concatenate ALL text parts (both input_text and output_text), so a
            // model text reply (stored as output_text) is preserved rather than
            // discarded. Returning the raw JSON array for an assistant turn would
            // send an unparseable content to DeepSeek.
            var sb = new System.Text.StringBuilder();
            foreach (var item in contentEl.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeEl)) continue;
                var typeStr = typeEl.GetString();
                if ((typeStr == "input_text" || typeStr == "output_text") &&
                    item.TryGetProperty("text", out var textEl))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(textEl.GetString() ?? "");
                }
            }
            if (sb.Length > 0) return sb.ToString();
        }

        return contentEl.ValueKind == JsonValueKind.String
            ? (contentEl.GetString() ?? "")
            : contentEl.GetRawText();
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
}

// ── DTOs for Responses API request body ──────────────────────────
internal sealed record ResponsesRequest
{
    public string? Model { get; init; }
    public JsonElement? Input { get; init; }
    public string? Instructions { get; init; }
    public int? MaxOutputTokens { get; init; }
    public bool? Stream { get; init; }
    public JsonElement? Tools { get; init; }
    public JsonElement? ToolChoice { get; init; }
}
