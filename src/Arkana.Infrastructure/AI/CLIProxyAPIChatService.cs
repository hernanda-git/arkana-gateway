using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// CLIProxyAPI provider — wraps subscription-based AI CLI tools
/// (Codex, Claude Code, Gemini CLI, Antigravity, Grok Build)
/// as an OpenAI-compatible API via the local CLIProxyAPI service.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 14b-2): wire-format construction moved to
/// <see cref="IDialectTranslator"/>. CLIProxyAPI speaks the OpenAI shape,
/// so the same OpenAI translator serves it. The connector's only
/// provider-specific work is building the full endpoint URL from the
/// configured <c>BaseUrl</c> (no <c>HttpClient.BaseAddress</c>).
/// </remarks>
internal sealed class CLIProxyAPIChatService : IChatCompletionService
{
    private readonly HttpClient _http;
    private readonly IProviderCatalog _catalog;
    private readonly ICredentialVault _vault;
    private readonly IDialectTranslator _translator;

    public string ProviderName => "CLIProxyAPI";

    public CLIProxyAPIChatService(
        HttpClient http,
        IProviderCatalog catalog,
        ICredentialVault vault,
        IDialectTranslator translator)
    {
        _http = http;
        _catalog = catalog;
        _vault = vault;
        _translator = translator;
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (request.TenantId == Guid.Empty)
                return new ChatResult
                {
                    ErrorMessage = "CLIProxyAPI request requires an authenticated tenant.",
                    Model = request.Model,
                    Duration = sw.Elapsed
                };

            // Resolve the provider that actually serves this request.
            // For multi-account Gemini (gemini-accN), each account is its own
            // AiProvider row with its own BaseUrl (a distinct cliproxy
            // container). The API key pins a preferred account via
            // PreferredProviderCode, which the handler stamps onto the request;
            // honor it so a key bound to gemini-acc2 reaches THAT account.
            // Fall back to the legacy single "cliproxyapi" provider for
            // backward compatibility.
            var targetCode = request.PreferredProviderCode;
            if (string.IsNullOrWhiteSpace(targetCode)
                || !targetCode.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
            {
                targetCode = "cliproxyapi";
            }

            var provider = await _catalog.GetByCodeAsync(targetCode, request.TenantId, ct);

            if (provider is null)
            {
                return new ChatResult
                {
                    ErrorMessage = $"CLIProxyAPI account provider '{targetCode}' is not configured",
                    Model = request.Model,
                    Duration = sw.Elapsed
                };
            }

            if (!provider.IsEnabled)
            {
                return new ChatResult
                {
                    ErrorMessage = $"CLIProxyAPI account provider '{targetCode}' is disabled",
                    Model = request.Model,
                    Duration = sw.Elapsed
                };
            }



            // BaseUrl — from DB, env var fallback, or default to localhost:12345
            var baseUrl = provider.BaseUrl
                ?? (targetCode.Equals("cliproxyapi", StringComparison.OrdinalIgnoreCase)
                    ? Environment.GetEnvironmentVariable("CLIPROXYAPI_BASE_URL")
                    : null);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return new ChatResult
                {
                    ErrorMessage = $"CLIProxyAPI account provider '{targetCode}' has no BaseUrl configured",
                    Model = request.Model,
                    Duration = sw.Elapsed
                };
            }

            // SECURITY: ApiKey column holds the sealed form. Decrypt just-in-time.
            var apiKey = targetCode.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)
                ? Environment.GetEnvironmentVariable("CLIPROXYAPI_API_KEY")
                : provider.DecryptApiKey(_vault)
                    ?? (targetCode.Equals("cliproxyapi", StringComparison.OrdinalIgnoreCase)
                        ? Environment.GetEnvironmentVariable("CLIPROXYAPI_API_KEY")
                        : null)
                    ?? string.Empty;

            var canonical = request.ToCanonical();
            var body = _translator.ToRequestBody(canonical);

            // Use full URL instead of relying on HttpClient's BaseAddress
            var endpoint = $"{baseUrl.TrimEnd('/')}/v1/chat/completions";
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(body)
            };

            if (!string.IsNullOrEmpty(apiKey))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var response = await _http.SendAsync(httpRequest, ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ChatResult
                {
                    ErrorMessage = $"CLIProxyAPI upstream request failed (HTTP {(int)response.StatusCode})",
                    UpstreamStatus = (int)response.StatusCode,
                    Duration = sw.Elapsed,
                    Model = request.Model,
                };
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            if (targetCode.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)
                && !HasValidGeminiCompatResponse(responseBody))
            {
                return new ChatResult
                {
                    ErrorMessage = "CLIProxyAPI Gemini returned an invalid response.",
                    Duration = sw.Elapsed,
                    Model = request.Model,
                };
            }
            var result = _translator.FromResponseBody(responseBody, request.Model, sw.Elapsed);

            if (!result.IsSuccess && result.ErrorMessage is not null
                && !result.ErrorMessage.StartsWith("CLIProxyAPI", StringComparison.Ordinal))
            {
                return result with { ErrorMessage = $"CLIProxyAPI: {result.ErrorMessage}" };
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            return new ChatResult { ErrorMessage = "CLIProxyAPI request was cancelled.", Duration = sw.Elapsed, Model = request.Model };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new ChatResult { ErrorMessage = "CLIProxyAPI upstream request timed out.", Duration = sw.Elapsed, Model = request.Model };
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return new ChatResult
            {
                ErrorMessage = ex.StatusCode is { } status
                    ? $"CLIProxyAPI upstream request failed (HTTP {(int)status})"
                    : "CLIProxyAPI request failed unexpectedly.",
                UpstreamStatus = ex.StatusCode is { } code ? (int)code : null,
                Duration = sw.Elapsed,
                Model = request.Model,
            };
        }
        catch (Exception)
        {
            sw.Stop();
            return new ChatResult
            {
                ErrorMessage = "CLIProxyAPI request failed unexpectedly.",
                Duration = sw.Elapsed,
                Model = request.Model,
            };
        }
    }

    private static bool HasValidGeminiCompatResponse(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var choices = document.RootElement.GetProperty("choices");
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return false;
            var choice = choices[0];
            var finishReason = choice.GetProperty("finish_reason");
            return finishReason.ValueKind == JsonValueKind.String
                && finishReason.GetString() is "stop" or "length"
                && choice.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
        catch (KeyNotFoundException) { return false; }
    }
}
