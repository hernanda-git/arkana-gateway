using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;
using Arkana.Infrastructure.AI.Dialects;
using System.Net.Http.Json;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Anthropic Messages API connector. Owns only the transport concerns:
/// auth headers (<c>x-api-key</c> + <c>anthropic-version</c>), endpoint
/// (the canonical <c>/v1/messages</c>), and provider lookup with env
/// var fallback. The request body and response parsing are delegated to
/// the <see cref="Dialects.AnthropicDialectTranslator"/>.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 / Phase 3 task 15b. This is the second connector built
/// on the dialect abstraction; the architecture proven in 14b-2
/// generalizes cleanly. The connector is intentionally tiny: ~70 lines
/// of which most is the auth header and error wrapping.
/// </remarks>
internal sealed class AnthropicChatService : IChatCompletionService
{
    /// <summary>
    /// Anthropic API version header value. Anthropic's docs require this
    /// on every Messages API request. Bumping it is a config decision
    /// (and an integration test) — not a code change.
    /// </summary>
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>
    /// Provider code used for the catalog lookup. The seed data in
    /// <c>GatewayDbContext</c> matches this.
    /// </summary>
    public const string ProviderCode = "anthropic";

    private readonly HttpClient _http;
    private readonly IProviderCatalog _catalog;
    private readonly ICredentialVault _vault;
    private readonly AnthropicDialectTranslator _translator;

    public string ProviderName => "Anthropic";

    public AnthropicChatService(
        HttpClient http,
        IProviderCatalog catalog,
        ICredentialVault vault,
        AnthropicDialectTranslator translator)
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
                    ErrorMessage = "Anthropic request requires an authenticated tenant.",
                    Duration = sw.Elapsed,
                    Model = request.Model,
                };

            // SECURITY: ApiKey column holds the sealed form. Decrypt
            // just-in-time per request. Falls back to env var if DB
            // has no key (e.g. fresh local dev).
            var provider = await _catalog.GetByCodeAsync(ProviderCode, request.TenantId, ct);
            string? apiKey = provider?.DecryptApiKey(_vault)
                ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                ?? string.Empty;

            var canonical = request.ToCanonical();
            var body = _translator.ToRequestBody(canonical);

            // Anthropic's /v1/messages is the only endpoint. The HTTP
            // client's BaseAddress should be https://api.anthropic.com/
            // (or a configurable override).
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
            {
                Content = JsonContent.Create(body)
            };

            if (!string.IsNullOrEmpty(apiKey))
            {
                // Anthropic uses x-api-key, NOT Authorization: Bearer.
                httpRequest.Headers.Add("x-api-key", apiKey);
                httpRequest.Headers.Add("anthropic-version", AnthropicVersion);
            }

            var response = await _http.SendAsync(httpRequest, ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ChatResult
                {
                    ErrorMessage = $"Anthropic upstream returned HTTP {(int)response.StatusCode}",
                    UpstreamStatus = (int)response.StatusCode,
                    Duration = sw.Elapsed,
                    Model = request.Model,
                };
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = _translator.FromResponseBody(responseBody, request.Model, sw.Elapsed);

            if (!result.IsSuccess && result.ErrorMessage is not null
                && !result.ErrorMessage.StartsWith("Anthropic", StringComparison.Ordinal))
            {
                return result with { ErrorMessage = $"Anthropic: {result.ErrorMessage}" };
            }

            return result;
        }
        catch (Exception)
                {
                    sw.Stop();
            return new ChatResult
            {
                ErrorMessage = "Anthropic: request failed.",
                Duration = sw.Elapsed,
                Model = request.Model,
            };
        }
    }
}
