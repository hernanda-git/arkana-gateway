using System.Text.Json;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// OpenAI provider connector (P1 fallback).
/// API key is fetched from the database at runtime.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 14b-2): the request body construction and
/// response parsing are now delegated to <see cref="IDialectTranslator"/>.
/// This connector owns only the transport concerns: HTTP endpoint,
/// auth header, provider lookup, error wrapping.
/// </remarks>
internal sealed class OpenAIChatService : IChatCompletionService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    private readonly HttpClient _http;
    private readonly IProviderCatalog _catalog;
    private readonly ICredentialVault _vault;
    private readonly IDialectTranslator _translator;

    public string ProviderName => "OpenAI";

    public OpenAIChatService(
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
                    ErrorMessage = "OpenAI request requires an authenticated tenant.",
                    Duration = sw.Elapsed,
                    Model = request.Model,
                };

            // SECURITY: ApiKey column holds the sealed form. Decrypt just-in-time.
            var provider = await _catalog.GetByCodeAsync("openai", request.TenantId, ct);
            string? apiKey = provider?.DecryptApiKey(_vault) ?? string.Empty;

            // Project to canonical, then ask the OpenAI dialect translator
            // for the wire body. The translator is shared with OpenCode,
            // DeepSeek, and CLIProxyAPI since they all speak the same shape.
            var canonical = request.ToCanonical();
            var body = _translator.ToRequestBody(canonical);
            var bodyJson = JsonSerializer.Serialize(body, JsonOpts);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrEmpty(apiKey))
            {
                httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            var response = await _http.SendAsync(httpRequest, ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ChatResult
                {
                    ErrorMessage = $"OpenAI upstream returned HTTP {(int)response.StatusCode}",
                    UpstreamStatus = (int)response.StatusCode,
                    Duration = sw.Elapsed,
                    Model = request.Model,
                };
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = _translator.FromResponseBody(responseBody, request.Model, sw.Elapsed);

            // Tag the result with the connector name so logs / dashboards
            // can attribute failures to the connector that observed them.
            if (!result.IsSuccess && result.ErrorMessage is not null
                && !result.ErrorMessage.StartsWith("OpenAI", StringComparison.Ordinal))
            {
                return result with { ErrorMessage = $"OpenAI: {result.ErrorMessage}" };
            }

            return result;
        }
        catch (Exception)
                {
                    sw.Stop();
            return new ChatResult
            {
                ErrorMessage = "OpenAI: request failed.",
                Duration = sw.Elapsed,
                Model = request.Model,
            };
        }
    }
}
