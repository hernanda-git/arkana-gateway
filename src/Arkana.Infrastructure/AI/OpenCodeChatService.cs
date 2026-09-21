using System.Net.Http.Headers;
using System.Text.Json;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// OpenCode provider — internal AI Gateway API (P0).
/// Assumes OpenAI-compatible API endpoint.
/// API key is fetched from the database at runtime, not stored in config.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 14b-2): wire-format construction moved to
/// <see cref="IDialectTranslator"/>. This connector owns transport only:
/// base URL resolution (DB → env), API key resolution (DB → env), and the
/// relative-path POST against the configured <c>HttpClient.BaseAddress</c>.
/// </remarks>
internal sealed class OpenCodeChatService : IChatCompletionService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    private readonly HttpClient _http;
    private readonly IProviderCatalog _catalog;
    private readonly ICredentialVault _vault;
    private readonly IDialectTranslator _translator;

    public string ProviderName => "OpenCode";

    public OpenCodeChatService(
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
                    ErrorMessage = "OpenCode request requires an authenticated tenant.",
                    Duration = sw.Elapsed,
                    Model = request.Model,
                };

            // Load API key from the in-memory provider catalog (PERF-ARKANA-001).
            // The catalog amortizes the DB read across many requests.
            var provider = await _catalog.GetByCodeAsync("opencode", request.TenantId, ct);

            // SECURITY: ApiKey column holds the sealed form. Decrypt just-in-time
            // for the upstream HTTP call. Falls back to env var if DB has no key.
            string? apiKey = provider?.DecryptApiKey(_vault)
                ?? Environment.GetEnvironmentVariable("OPENCODE_GO_API_KEY")
                ?? string.Empty;

            var canonical = request.ToCanonical();
            var body = _translator.ToRequestBody(canonical);
            var bodyJson = JsonSerializer.Serialize(body, JsonOpts);

            // Relative path — relies on HttpClient.BaseAddress being set in DI
            // to https://opencode.ai/zen/go/v1 (or the override).
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json")
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
                    ErrorMessage = $"OpenCode upstream returned HTTP {(int)response.StatusCode}",
                    UpstreamStatus = (int)response.StatusCode,
                    Duration = sw.Elapsed,
                    Model = request.Model,
                };
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = _translator.FromResponseBody(responseBody, request.Model, sw.Elapsed);

            if (!result.IsSuccess && result.ErrorMessage is not null
                && !result.ErrorMessage.StartsWith("OpenCode", StringComparison.Ordinal))
            {
                return result with { ErrorMessage = $"OpenCode: {result.ErrorMessage}" };
            }

            return result;
        }
        catch (Exception)
                {
                    sw.Stop();
            return new ChatResult
            {
                ErrorMessage = "OpenCode: request failed.",
                Duration = sw.Elapsed,
                Model = request.Model,
            };
        }
    }
}
