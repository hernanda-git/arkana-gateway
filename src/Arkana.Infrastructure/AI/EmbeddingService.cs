using System.Net.Http.Json;
using System.Text.Json;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Generates embeddings by proxying /v1/embeddings to the upstream provider.
/// Resolves the provider from the model-to-provider catalog, same as chat.
/// </summary>
/// <remarks>AI-ARKANA-006 (Phase 3, task 18).</remarks>
internal sealed class EmbeddingService : IEmbeddingService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly HttpClient _http;
    private readonly IProviderCatalog _catalog;
    private readonly ICredentialVault _vault;
    private readonly IServiceScopeFactory _scopeFactory;

    public string ProviderName => "opencode";

    public EmbeddingService(
        IHttpClientFactory httpClientFactory,
        IProviderCatalog catalog,
        ICredentialVault vault,
        IServiceScopeFactory scopeFactory)
    {
        _http = httpClientFactory.CreateClient("opencode");
        _catalog = catalog;
        _vault = vault;
        _scopeFactory = scopeFactory;
    }

    public async Task<EmbeddingResult> GenerateEmbeddingsAsync(
        Guid tenantId,
        string model,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        // ── Resolve provider for this model ────────────────────
        string baseUrl = "https://opencode.ai/zen/go/v1";
        string? apiKey = null;

        try
        {
            if (tenantId == Guid.Empty)
                return new EmbeddingResult { IsSuccess = false, ErrorMessage = "Authenticated tenant is required." };

            await using var scope = _scopeFactory.CreateAsyncScope();
            var modelRepo = scope.ServiceProvider.GetRequiredService<IModelRepository>();
            var modelConfig = await modelRepo.GetByCodeAsync(model, tenantId, cancellationToken);
            if (modelConfig?.Provider is not null)
            {
                if (!string.IsNullOrEmpty(modelConfig.Provider.BaseUrl))
                    baseUrl = modelConfig.Provider.BaseUrl;

                var decrypted = modelConfig.Provider.DecryptApiKey(_vault);
                if (!string.IsNullOrEmpty(decrypted))
                    apiKey = decrypted;
            }
        }
        catch
        {
            // Fall through to defaults
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            // Try the default provider via catalog
            var defaultProvider = await _catalog.GetByCodeAsync("opencode", tenantId, cancellationToken);
            if (defaultProvider is not null)
                apiKey = defaultProvider.DecryptApiKey(_vault);
        }

        // ── Build the request ──────────────────────────────────
        var requestBody = new
        {
            model,
            input = inputs.Count == 1 ? inputs[0] : (object)inputs,
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/v1/embeddings")
        {
            Content = JsonContent.Create(requestBody, options: JsonOpts),
        };

        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

        // ── Send and parse ─────────────────────────────────────
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception)
        {
            return new EmbeddingResult
            {
                IsSuccess = false,
                ErrorMessage = "Embedding request failed.",
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return new EmbeddingResult
            {
                IsSuccess = false,
                ErrorMessage = $"Upstream returned HTTP {(int)response.StatusCode}.",
            };
        }

        // ── Parse the response ─────────────────────────────────
        try
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var dataList = new List<EmbeddingVector>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var idx = item.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                    var vec = new List<float>();
                    if (item.TryGetProperty("embedding", out var emb) && emb.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in emb.EnumerateArray())
                            vec.Add((float)v.GetDouble());
                    }
                    dataList.Add(new EmbeddingVector { Index = idx, Embedding = vec });
                }
            }

            var modelName = root.TryGetProperty("model", out var m) ? m.GetString() ?? model : model;
            var totalTokens = 0;
            if (root.TryGetProperty("usage", out var usage) &&
                usage.TryGetProperty("total_tokens", out var tt))
            {
                totalTokens = tt.GetInt32();
            }

            return new EmbeddingResult
            {
                Data = dataList,
                Model = modelName,
                TotalTokens = totalTokens,
                IsSuccess = true,
            };
        }
        catch (Exception)
        {
            return new EmbeddingResult
            {
                IsSuccess = false,
                ErrorMessage = "Failed to parse embedding response.",
            };
        }
    }
}
