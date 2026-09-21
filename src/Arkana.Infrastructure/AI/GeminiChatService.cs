using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arkana.Domain.Entities;
using Microsoft.Extensions.Logging;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Infrastructure.AI.Dialects;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Google Gemini API connector. Owns transport concerns: auth header
/// (<c>x-goog-api-key</c>), endpoint (<c>/models/{model}:generateContent</c>),
/// and provider lookup with env var fallback. Request body and response
/// parsing are delegated to <see cref="GeminiDialectTranslator"/>.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 / Phase 3 task 15d. Pattern follows
/// <see cref="AnthropicChatService"/> — the connector is intentionally
/// thin (~70 lines), with dialect concerns in the translator.
///
/// Gemini auth:
///   Uses <c>x-goog-api-key</c> header (NOT Authorization: Bearer).
///   API key is resolved from the DB (sealed, decrypted via
///   <see cref="ICredentialVault"/>) with env var fallback
///   (<c>GEMINI_API_KEY</c>).
///
/// Endpoint:
///   POST /v1beta/models/{model}:generateContent
///   The connector injects the model name into the URL path; the
///   translator's <c>ToRequestBody</c> omits the model field (Gemini
///   doesn't accept it in the body).
/// </remarks>
internal sealed class GeminiChatService : IChatCompletionService, IStreamingChatCompletionService
{
    /// <summary>
    /// Provider code used for the catalog lookup. Seed data in
    /// <c>GatewayDbContext</c> matches this.
    /// </summary>
    public const string ProviderCode = "gemini";

    private readonly HttpClient _http;
    private readonly IProviderCatalog _catalog;
    private readonly ICredentialVault _vault;
    private readonly GeminiDialectTranslator _translator;
    private readonly ILogger<GeminiChatService> _logger;
    private readonly IOAuthTokenResolver? _oauthResolver;
    private readonly ITenantProvider? _tenantProvider;

    public string ProviderName => "Gemini";

    public GeminiChatService(
        HttpClient http,
        IProviderCatalog catalog,
        ICredentialVault vault,
        GeminiDialectTranslator translator,
        ILogger<GeminiChatService> logger,
        IOAuthTokenResolver? oauthResolver = null,
        ITenantProvider? tenantProvider = null)
    {
        _http = http;
        _catalog = catalog;
        _vault = vault;
        _translator = translator;
        _logger = logger;
        _oauthResolver = oauthResolver;
        _tenantProvider = tenantProvider;
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var credential = await ResolveCredentialAsync(request, ct);
            if (credential.Error is not null)
                return Failure(credential.Error, request.Model, sw.Elapsed);

            var canonical = request.ToCanonical();
            var body = _translator.ToRequestBody(canonical);

            // Gemini embeds the model name in the URL path, not the body.
            var modelPath = Uri.EscapeDataString(UpstreamModelName(request.Model));
            var endpoint = $"models/{modelPath}:generateContent";

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(body),
            };

            if (credential.IsBearer)
                httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Value);
            else
                httpRequest.Headers.Add("x-goog-api-key", credential.Value);

            using var response = await _http.SendAsync(httpRequest, ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemini upstream HTTP failure. status={StatusCode}", (int)response.StatusCode);
                return new ChatResult
                {
                    ErrorMessage = GeminiErrorPolicy.ForStatus(response.StatusCode),
                    UpstreamStatus = (int)response.StatusCode,
                    Duration = sw.Elapsed,
                    Model = request.Model,
                    RouteKind = "native",
                };
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = _translator.FromResponseBody(responseBody, request.Model, sw.Elapsed);

            if (!result.IsSuccess && result.ErrorMessage is not null
                && !result.ErrorMessage.StartsWith("Gemini", StringComparison.Ordinal))
            {
                return result with { ErrorMessage = $"Gemini: {result.ErrorMessage}" };
            }

            return result with
            {
                RouteKind = "native",
                ResolvedProviderAccountId = request.PreferredProviderAccountId,
                ResolvedProviderAccountCode = request.PreferredProviderAccountCode,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogInformation("Gemini request cancelled.");
            return Failure("Gemini request was cancelled.", request.Model, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning("Gemini request timed out.");
            return Failure("Gemini upstream request timed out.", request.Model, sw.Elapsed);
        }
        catch (JsonException)
        {
            sw.Stop();
            _logger.LogWarning("Gemini returned malformed JSON.");
            return Failure("Gemini returned an invalid response.", request.Model, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning("Gemini request failed with {ExceptionType}.", ex.GetType().Name);
            return Failure("Gemini request failed unexpectedly.", request.Model, sw.Elapsed);
        }
    }

    public async Task<ChatStreamResult> CompleteStreamingAsync(ChatRequest request, CancellationToken ct = default)
    {
        HttpResponseMessage? response = null;
        try
        {
            var credential = await ResolveCredentialAsync(request, ct);
            if (credential.Error is not null)
                return new ChatStreamResult(null, Failure(credential.Error, request.Model, TimeSpan.Zero));
            var body = _translator.ToRequestBody(request.ToCanonical());
            var modelPath = Uri.EscapeDataString(UpstreamModelName(request.Model));
            var endpoint = $"models/{modelPath}:streamGenerateContent?alt=sse";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(body),
            };
            if (credential.IsBearer)
                httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Value);
            else
                httpRequest.Headers.Add("x-goog-api-key", credential.Value);

            response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var result = new ChatResult
                {
                    ErrorMessage = GeminiErrorPolicy.ForStatus(response.StatusCode),
                    UpstreamStatus = (int)response.StatusCode,
                    Model = request.Model,
                    RouteKind = "native",
                };
                response.Dispose();
                response = null;
                return new ChatStreamResult(null, result);
            }

            var stream = await response.Content.ReadAsStreamAsync(ct);
            var heldResponse = response;
            response = null;
            return new ChatStreamResult(
                stream,
                new ChatResult
                {
                    Model = request.Model,
                    RouteKind = "native",
                    ResolvedProviderAccountId = request.PreferredProviderAccountId,
                    ResolvedProviderAccountCode = request.PreferredProviderAccountCode,
                },
                async _ =>
                {
                    await stream.DisposeAsync();
                    heldResponse.Dispose();
                });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            response?.Dispose();
            return new ChatStreamResult(null, new ChatResult
            {
                ErrorMessage = "Gemini request was cancelled.",
                Model = request.Model,
                RouteKind = "native",
            });
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            return new ChatStreamResult(null, new ChatResult
            {
                ErrorMessage = "Gemini upstream request timed out.",
                Model = request.Model,
                RouteKind = "native",
            });
        }
        catch (Exception ex)
        {
            response?.Dispose();
            _logger.LogWarning("Gemini streaming request failed with {ExceptionType}.", ex.GetType().Name);
            return new ChatStreamResult(null, new ChatResult
            {
                ErrorMessage = "Gemini streaming request failed unexpectedly.",
                Model = request.Model,
                RouteKind = "native",
            });
        }
    }

    private async Task<GeminiCredential> ResolveCredentialAsync(ChatRequest request, CancellationToken ct)
    {
        if (request.TenantId == Guid.Empty)
            return new(null, false, "Authenticated tenant is required.");

        var code = string.IsNullOrWhiteSpace(request.PreferredProviderCode)
            ? ProviderCode
            : request.PreferredProviderCode.Trim();
        var provider = await _catalog.GetByCodeAsync(code, request.TenantId, ct);

        if (provider is null || (request.PreferredProviderId is { } requestedProviderId
            && provider.Id != requestedProviderId))
            return new(null, false, "Gemini provider ownership is unavailable.", provider);

        if (provider?.UsesOAuth == true)
        {
            if (_oauthResolver is null)
                return new(null, false, "Gemini OAuth token resolver is not configured.");

            var bearer = await _oauthResolver.GetBearerTokenAsync(provider.Id, request.TenantId, ct);
            return string.IsNullOrWhiteSpace(bearer)
                ? new(null, true, "Gemini OAuth credential is unavailable; reconnect is required.", provider)
                : new(bearer, true, null, provider);
        }

        if (provider is not null)
        {
            if (code.StartsWith("gemini-acc", StringComparison.OrdinalIgnoreCase))
                return new(null, false, "Gemini account ownership is not configured for API-key transport.", provider);

            var apiKey = provider.DecryptApiKey(_vault);
            if (!string.IsNullOrWhiteSpace(apiKey))
                return new(apiKey, false, null, provider);
        }

        if (code.Equals(ProviderCode, StringComparison.OrdinalIgnoreCase))
        {
            var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
                return new(envKey, false, null, provider);
        }

        return new(null, false, "Gemini API credential is not configured.", provider);
    }

    /// <summary>
    /// Dashboard-created per-account models use a stable <c>-accN</c> suffix so
    /// they remain unique in the tenant model catalog. Native Gemini's upstream
    /// API does not know that alias and must receive the base model name.
    /// </summary>
    private static string UpstreamModelName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return "gemini-2.0-flash";

        const string accountMarker = "-acc";
        if (!model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
            return model;

        var markerIndex = model.LastIndexOf(accountMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= "gemini-".Length || markerIndex + accountMarker.Length >= model.Length)
            return model;

        var suffix = model[(markerIndex + accountMarker.Length)..];
        return suffix.All(static c => c is >= '0' and <= '9')
            ? model[..markerIndex]
            : model;
    }

    private sealed record GeminiCredential(
        string? Value,
        bool IsBearer,
        string? Error,
        AiProvider? Provider = null);

    private static ChatResult Failure(string message, string model, TimeSpan duration) => new()
    {
        ErrorMessage = message,
        Duration = duration,
        Model = model,
        RouteKind = "native",
    };
}

/// <summary>State retained while translating one native Gemini SSE response.</summary>
public sealed class GeminiNativeStreamState
{
    internal bool RoleSent { get; set; }
    public bool SawFinish { get; internal set; }
    internal int NextToolCallIndex { get; set; }
}

/// <summary>Converts bounded native Gemini stream envelopes to Chat Completions chunks.</summary>
public static class GeminiNativeStreamTranslator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool TryTranslate(
        string data,
        string model,
        GeminiNativeStreamState state,
        out string? openAiData,
        out int promptTokens,
        out int completionTokens)
    {
        openAiData = null;
        promptTokens = 0;
        completionTokens = 0;
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
            return false;

        if (root.TryGetProperty("usageMetadata", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("promptTokenCount", out var prompt) && prompt.TryGetInt32(out var promptValue))
                promptTokens = promptValue;
            if (usage.TryGetProperty("candidatesTokenCount", out var completion) && completion.TryGetInt32(out var completionValue))
                completionTokens = completionValue;
        }

        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0)
            return true;

        var candidate = candidates[0];
        string? finishReason = null;
        if (candidate.TryGetProperty("finishReason", out var finish)
            && finish.ValueKind == JsonValueKind.String)
        {
            finishReason = finish.GetString()?.ToUpperInvariant() switch
            {
                "STOP" => "stop",
                "MAX_TOKENS" => "length",
                "SAFETY" => "content_filter",
                _ => null,
            };
            if (finishReason is null)
                return false;
        }

        var delta = new JsonObject();
        if (candidate.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("parts", out var parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            var text = new System.Text.StringBuilder();
            var toolCalls = new JsonArray();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textPart) && textPart.ValueKind == JsonValueKind.String)
                    text.Append(textPart.GetString());
                if (part.TryGetProperty("functionCall", out var functionCall)
                    && functionCall.ValueKind == JsonValueKind.Object)
                {
                    var name = functionCall.TryGetProperty("name", out var nameElement)
                        ? nameElement.GetString() ?? "unknown"
                        : "unknown";
                    var toolIndex = state.NextToolCallIndex++;
                    var toolId = $"gemini:{name}:{toolIndex}";
                    var arguments = functionCall.TryGetProperty("args", out var args)
                        ? args.GetRawText()
                        : "{}";
                    toolCalls.Add(new JsonObject
                    {
                        ["index"] = toolIndex,
                        ["id"] = toolId,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = name,
                            ["arguments"] = arguments,
                        },
                    });
                }
            }
            if (text.Length > 0)
                delta["content"] = text.ToString();
            if (toolCalls.Count > 0)
                delta["tool_calls"] = toolCalls;
        }

        if (!state.RoleSent && delta.Count > 0)
        {
            delta["role"] = "assistant";
            state.RoleSent = true;
        }

        if (finishReason is not null)
            state.SawFinish = true;

        if (delta.Count == 0 && finishReason is null)
            return true;

        var choice = new JsonObject
        {
            ["index"] = candidate.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var indexValue) ? indexValue : 0,
            ["delta"] = delta,
            ["finish_reason"] = finishReason,
        };
        var output = new JsonObject
        {
            ["id"] = "chatcmpl-gemini-stream",
            ["object"] = "chat.completion.chunk",
            ["model"] = model,
            ["choices"] = new JsonArray(choice),
        };
        if (promptTokens > 0 || completionTokens > 0)
            output["usage"] = new JsonObject
            {
                ["prompt_tokens"] = promptTokens,
                ["completion_tokens"] = completionTokens,
                ["total_tokens"] = promptTokens + completionTokens,
            };
        openAiData = output.ToJsonString(JsonOptions);
        return true;
    }
}

internal static class GeminiErrorPolicy
{
    public static string ForStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "Gemini authentication failed.",
        HttpStatusCode.Forbidden => "Gemini authorization failed.",
        (HttpStatusCode)429 => "Gemini rate limit exceeded.",
        _ when (int)status >= 500 => "Gemini upstream service is unavailable.",
        _ => $"Gemini upstream request failed (HTTP {(int)status}).",
    };
}
