using System.Text;
using System.Text.Json;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// Qdrant-backed semantic cache for chat completions (AI-ARKANA-005).
///
/// How it works:
///   1. A chat request is converted to a flat text string (all messages
///      concatenated with role prefixes + model name disambiguation).
///   2. That text is embedded via <see cref="IEmbeddingService"/>.
///   3. The vector is searched (or stored) in a Qdrant collection.
///   4. On a hit, the stored <see cref="CachedResponse"/> payload is
///      deserialized and returned if the cosine similarity exceeds the
///      configured threshold.
///
/// Thread safety: QdrantClient is thread-safe. The same instance is
/// shared across all requests (singleton lifetime).
///
/// Error isolation: every public method catches and logs exceptions,
/// never throwing to the caller. A cache failure is always degraded
/// gracefully — the caller falls through to the upstream provider.
/// </summary>
internal sealed class QdrantSemanticCache : ISemanticCache, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<QdrantSemanticCache> _logger;
    private readonly SemanticCacheOptions _options;
    private readonly QdrantClient _client;
    private readonly string _collectionName;
    private readonly object _initLock = new();
    private bool _initialized;

    // Metric counters — exposed for the metrics exporter.
    private long _hits;
    private long _misses;
    private long _writes;

    public long Hits => System.Threading.Interlocked.Read(ref _hits);
    public long Misses => System.Threading.Interlocked.Read(ref _misses);
    public long SemanticWrites => System.Threading.Interlocked.Read(ref _writes);

    public QdrantSemanticCache(
        IEmbeddingService embeddingService,
        IOptions<SemanticCacheOptions> options,
        ILogger<QdrantSemanticCache> logger)
    {
        _embeddingService = embeddingService;
        _logger = logger;
        _options = options.Value;
        _collectionName = _options.CollectionName;

        _client = new QdrantClient(
            host: _options.QdrantHost,
            port: _options.QdrantPort,
            https: _options.QdrantUseTls);
    }

    // ────────────────────────────────────────────────────────────────
    //  ISemanticCache
    // ────────────────────────────────────────────────────────────────

    public async Task<CachedResponse?> GetSimilarAsync(
        ChatRequest chatRequest,
        CancellationToken ct = default)
    {
        try
        {
            await InitializeAsync(ct).ConfigureAwait(false);

            var text = BuildQueryText(chatRequest);

            var embedding = await EmbedTextAsync(text, chatRequest.TenantId, ct).ConfigureAwait(false);
            if (embedding is null) return null;

            var results = await _client.SearchAsync(
                collectionName: _collectionName,
                vector: new ReadOnlyMemory<float>(embedding.ToArray()),
                limit: _options.MaxResults,
                filter: new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "model",
                        Match = new Match { Text = chatRequest.Model },
                    },
                },
                cancellationToken: ct).ConfigureAwait(false);

            if (results.Count == 0)
            {
                System.Threading.Interlocked.Increment(ref _misses);
                _logger.LogDebug(
                    "Semantic cache MISS for model {Model} (no results)", chatRequest.Model);
                return null;
            }

            var best = results[0];
            if (best.Score < _options.SimilarityThreshold)
            {
                System.Threading.Interlocked.Increment(ref _misses);
                _logger.LogDebug(
                    "Semantic cache MISS for model {Model} (score={Score:F4} < threshold={Threshold})",
                    chatRequest.Model, best.Score, _options.SimilarityThreshold);
                return null;
            }

            // Deserialize the payload from the stored point.
            var cached = DeserializePayload(best.Payload);
            if (cached is null)
            {
                _logger.LogWarning(
                    "Semantic cache: found point for model {Model} but payload was unparseable",
                    chatRequest.Model);
                return null;
            }

            _logger.LogInformation(
                "Semantic cache HIT for model {Model} (score={Score:F4}, served_by={Provider})",
                chatRequest.Model, best.Score, cached.ServedByProviderName);

            System.Threading.Interlocked.Increment(ref _hits);
            return cached;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Semantic cache lookup failed for model {Model} — falling through to upstream",
                chatRequest.Model);
            return null;
        }
    }

    public async Task StoreAsync(
        ChatRequest chatRequest,
        CachedResponse response,
        CancellationToken ct = default)
    {
        try
        {
            await InitializeAsync(ct).ConfigureAwait(false);

            var text = BuildQueryText(chatRequest);

            var embedding = await EmbedTextAsync(text, chatRequest.TenantId, ct).ConfigureAwait(false);
            if (embedding is null) return;

            var ttl = _options.DefaultTtl;
            ulong? ttlSeconds = ttl > TimeSpan.Zero ? (ulong)ttl.TotalSeconds : null;

            var payload = SerializePayload(response);

            var point = new PointStruct
            {
                Id = new PointId { Uuid = Guid.NewGuid().ToString() },
                Vectors = embedding.ToArray(),
            };
            point.Payload["response"] = payload["response"];
            point.Payload["model"] = payload["model"];
            point.Payload["provider"] = payload["provider"];
            point.Payload["cached_at"] = payload["cached_at"];
            if (ttlSeconds.HasValue)
                point.Payload["_ttl"] = ttlSeconds.Value;

            await _client.UpsertAsync(
                collectionName: _collectionName,
                points: [point],
                cancellationToken: ct).ConfigureAwait(false);

            System.Threading.Interlocked.Increment(ref _writes);

            _logger.LogDebug(
                "Semantic cache STORE for model {Model} (ttl={Ttl}s)",
                chatRequest.Model, ttlSeconds ?? 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cache writes must NEVER break the caller. Log and swallow.
            _logger.LogWarning(ex,
                "Semantic cache write failed for model {Model} — response delivered upstream",
                chatRequest.Model);
        }
    }

    public async Task InvalidateAllAsync(CancellationToken ct = default)
    {
        try
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            await _client.DeleteCollectionAsync(_collectionName, timeout: null, cancellationToken: ct).ConfigureAwait(false);
            _initialized = false;
            _logger.LogInformation("Semantic cache cleared (collection deleted)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to clear semantic cache");
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Collection initialization
    // ────────────────────────────────────────────────────────────────

    private async Task InitializeAsync(CancellationToken ct)
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;
            // Double-checked lock: set flag early so concurrent callers
            // don't all try to create the collection. The worst case is
            // a harmless "collection already exists" error from Qdrant.
            _initialized = true;
        }

        try
        {
            var exists = await _client.CollectionExistsAsync(_collectionName, ct).ConfigureAwait(false);
            if (!exists)
            {
                await _client.CreateCollectionAsync(
                    collectionName: _collectionName,
                    vectorsConfig: new VectorParams
                    {
                        Size = _options.VectorSize,
                        Distance = Distance.Cosine,
                    },
                    cancellationToken: ct).ConfigureAwait(false);

                _logger.LogInformation(
                    "Created Qdrant collection '{Collection}' (size={Size}, distance=Cosine)",
                    _collectionName, _options.VectorSize);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Failed to initialize Qdrant collection '{Collection}' — semantic cache degraded",
                _collectionName);
            // Allow the caller to proceed; subsequent operations will
            // hit this again and fail gracefully.
            _initialized = false;
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Embedding helpers
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a flat text representation of the chat request for
    /// embedding. The text includes role-prefixed messages and the
    /// model name, so requests to different models produce different
    /// vectors even when the message content is identical.
    /// </summary>
    private static string BuildQueryText(ChatRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("model:").Append(request.Model.ToLowerInvariant()).Append('\n');

        foreach (var msg in request.Messages)
        {
            sb.Append(msg.Role).Append(':');
            sb.Append(msg.Content ?? "");
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generate a single embedding vector for the given text.
    /// Returns null on any error (caller handles gracefully).
    /// </summary>
    private async Task<IReadOnlyList<float>?> EmbedTextAsync(string text, Guid tenantId, CancellationToken ct)
    {
        var result = await _embeddingService.GenerateEmbeddingsAsync(
            tenantId,
            "text-embedding-3-small",
            [text],
            ct).ConfigureAwait(false);

        if (!result.IsSuccess || result.Data.Count == 0)
        {
            _logger.LogWarning(
                "Failed to generate embedding for semantic cache: {Error}",
                result.ErrorMessage ?? "no data returned");
            return null;
        }

        return result.Data[0].Embedding;
    }

    // ────────────────────────────────────────────────────────────────
    //  Payload serialization
    // ────────────────────────────────────────────────────────────────

    private static Dictionary<string, Value> SerializePayload(CachedResponse response)
    {
        var json = JsonSerializer.Serialize(response, JsonOpts);
        return new Dictionary<string, Value>
        {
            ["response"] = json,
            ["model"] = response.Model,
            ["provider"] = response.ServedByProviderName,
            ["cached_at"] = response.CachedAt.ToString("O"),
        };
    }

    private static CachedResponse? DeserializePayload(MapField<string, Value> payload)
    {
        if (!payload.TryGetValue("response", out var value) || string.IsNullOrEmpty(value.StringValue))
            return null;

        try
        {
            return JsonSerializer.Deserialize<CachedResponse>(value.StringValue, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Cleanup
    // ────────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await Task.CompletedTask;
    }
}
