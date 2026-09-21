using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Shared transport logic for OpenAI-compat providers. Five new providers
/// (Groq, OpenRouter, Qwen, GLM, Cloudflare) plug in as tiny subclasses
/// that only set <see cref="ProviderName"/>, <see cref="ProviderCode"/>,
/// the endpoint, and an optional env-var API-key fallback.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 16). All five new providers speak the
/// OpenAI <c>/v1/chat/completions</c> shape, so they share
/// <see cref="OpenAiDialectTranslator"/>. The base class owns:
/// <list type="bullet">
///   <item>Provider catalog lookup with the configured <see cref="ProviderCode"/></item>
///   <item>API key resolution (DB -&gt; env-var fallback -&gt; empty)</item>
///   <item>Request body serialization (canonical -&gt; wire via translator)</item>
///   <item>HTTP POST with Bearer auth (the OpenAI-compat convention)</item>
///   <item>Error wrapping with the connector name in the message</item>
/// </list>
/// Subclasses are 5-10 lines: just set the four abstract/virtual members.
/// </remarks>
internal abstract class OpenAiCompatChatServiceBase : IChatCompletionService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    private readonly HttpClient _http;
    private readonly IProviderCatalog _catalog;
    private readonly ICredentialVault _vault;
    private readonly IDialectTranslator _translator;
    private readonly IOAuthTokenResolver? _oauthResolver;

    /// <summary>ProviderName as it appears in logs and the factory's index.</summary>
    public abstract string ProviderName { get; }

    /// <summary>
    /// Code used for the in-memory provider catalog lookup. Typically the
    /// lowercased <c>ProviderName</c> (e.g. <c>"groq"</c>).
    /// </summary>
    protected abstract string ProviderCode { get; }

    /// <summary>
    /// Endpoint path. Either a full URL (https://...) or a relative
    /// path (e.g. <c>"v1/chat/completions"</c>) when the HttpClient's
    /// BaseAddress is set in DI.
    /// </summary>
    protected abstract string Endpoint { get; }

    /// <summary>
    /// Env-var name to consult when the catalog has no API key. Return
    /// <c>null</c> to skip the fallback (the connector is then
    /// DB-only).
    /// </summary>
    protected abstract string? EnvVarApiKey { get; }

    /// <summary>
    /// <c>true</c> to use <see cref="JsonContent.Create"/> for the
    /// request body (typed, sets Content-Type automatically); <c>false</c>
    /// to use <c>StringContent</c> with explicit UTF-8 JSON. Today
    /// every OpenAI-compat provider accepts either, so subclasses
    /// override only for testing. Default: <c>true</c>.
    /// </summary>
    protected virtual bool UseTypedJsonContent => true;

    protected OpenAiCompatChatServiceBase(
        HttpClient http,
        IProviderCatalog catalog,
        ICredentialVault vault,
        IDialectTranslator translator,
        IOAuthTokenResolver? oauthResolver = null)
    {
        _http = http;
        _catalog = catalog;
        _vault = vault;
        _translator = translator;
        _oauthResolver = oauthResolver;
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (request.TenantId == Guid.Empty)
                return new ChatResult
                {
                    ErrorMessage = $"{ProviderName} request requires an authenticated tenant.",
                    Duration = sw.Elapsed,
                    Model = request.Model,
                };

            // SECURITY: ApiKey column holds the sealed form. Decrypt
            // just-in-time. Falls back to env var when DB has no key.
            // When the provider uses OAuth, the live (auto-refreshed) bearer
            // token takes precedence; the gateway brokers the credential.
            var provider = await _catalog.GetByCodeAsync(ProviderCode, request.TenantId, ct);
            string? apiKey;
            if (provider is { UsesOAuth: true } && _oauthResolver is not null)
            {
                apiKey = await _oauthResolver.GetBearerTokenAsync(provider.Id, request.TenantId, ct) ?? string.Empty;
            }
            else
            {
                apiKey = provider?.DecryptApiKey(_vault)
                    ?? (EnvVarApiKey is { } env ? Environment.GetEnvironmentVariable(env) : null)
                    ?? string.Empty;
            }

            // Project to canonical, then ask the OpenAI dialect
            // translator for the wire body. All five subclasses share
            // this translator.
            var canonical = request.ToCanonical();
            var body = _translator.ToRequestBody(canonical);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            if (UseTypedJsonContent)
            {
                httpRequest.Content = JsonContent.Create(body);
            }
            else
            {
                var bodyJson = JsonSerializer.Serialize(body, JsonOpts);
                httpRequest.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            }

            if (!string.IsNullOrEmpty(apiKey))
            {
                httpRequest.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            var response = await _http.SendAsync(httpRequest, ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ChatResult
                {
                    ErrorMessage = $"{ProviderName} upstream returned HTTP {(int)response.StatusCode}",
                    UpstreamStatus = (int)response.StatusCode,
                    Duration = sw.Elapsed,
                    Model = request.Model,
                };
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = _translator.FromResponseBody(responseBody, request.Model, sw.Elapsed);

            // Tag any error with the connector name. If the translator's
            // error message already starts with our name, leave it.
            if (!result.IsSuccess && result.ErrorMessage is not null
                && !result.ErrorMessage.StartsWith(ProviderName, StringComparison.Ordinal))
            {
                return result with { ErrorMessage = $"{ProviderName}: {result.ErrorMessage}" };
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            return new ChatResult { ErrorMessage = $"{ProviderName} request was cancelled.", Duration = sw.Elapsed, Model = request.Model };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new ChatResult { ErrorMessage = $"{ProviderName} upstream request timed out.", Duration = sw.Elapsed, Model = request.Model };
        }
        catch (Exception)
        {
            sw.Stop();
            return new ChatResult
            {
                ErrorMessage = $"{ProviderName} request failed unexpectedly.",
                Duration = sw.Elapsed,
                Model = request.Model,
            };
        }
    }
}
