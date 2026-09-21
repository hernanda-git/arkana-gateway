using System.Net.Http.Headers;
using System.Net.Http.Json;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// DeepSeek Direct provider connector — bypasses opencode.ai to reach
/// api.deepseek.com directly. Supports function/tool calling for agent CLIs.
/// API key is fetched from the database at runtime.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 14b-2): wire-format construction moved to
/// <see cref="IDialectTranslator"/>. DeepSeek speaks the OpenAI
/// <c>/v1/chat/completions</c> shape, so the same OpenAI translator serves it.
/// </remarks>
internal sealed class DeepSeekChatService : IChatCompletionService
{
    private readonly HttpClient _http;
    private readonly IProviderCatalog _catalog;
    private readonly ICredentialVault _vault;
    private readonly IDialectTranslator _translator;

    public string ProviderName => "DeepSeek";

    public DeepSeekChatService(
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
                    ErrorMessage = "DeepSeek request requires an authenticated tenant.",
                    Duration = sw.Elapsed,
                    Model = request.Model,
                };

            // Load API key from the in-memory provider catalog (PERF-ARKANA-001).
            var provider = await _catalog.GetByCodeAsync("deepseek", request.TenantId, ct);

            // SECURITY: ApiKey column holds the sealed form. Decrypt just-in-time.
            string? apiKey = provider?.DecryptApiKey(_vault) ?? string.Empty;

            // Resolve the base URL from the DB provider; fall back to default.
            var baseUrl = provider?.BaseUrl?.TrimEnd('/') ?? "https://api.deepseek.com/v1";

            var canonical = request.ToCanonical();
            var body = _translator.ToRequestBody(canonical);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
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
                    ErrorMessage = $"DeepSeek upstream returned HTTP {(int)response.StatusCode}",
                    UpstreamStatus = (int)response.StatusCode,
                    Duration = sw.Elapsed,
                    Model = request.Model,
                };
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = _translator.FromResponseBody(responseBody, request.Model, sw.Elapsed);

            if (!result.IsSuccess && result.ErrorMessage is not null
                && !result.ErrorMessage.StartsWith("DeepSeek", StringComparison.Ordinal))
            {
                return result with { ErrorMessage = $"DeepSeek: {result.ErrorMessage}" };
            }

            return result;
        }
        catch (Exception)
                {
                    sw.Stop();
            return new ChatResult
            {
                ErrorMessage = "DeepSeek: request failed.",
                Duration = sw.Elapsed,
                Model = request.Model,
            };
        }
    }
}
