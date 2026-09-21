namespace Arkana.Domain.Services;

/// <summary>
/// Configuration for the semantic cache (AI-ARKANA-005).
///
/// Bound from configuration section <c>SemanticCache</c>:
/// <code>
/// "SemanticCache": {
///   "Enabled": false,
///   "SimilarityThreshold": 0.92,
///   "MaxResults": 1,
///   "DefaultTtlSeconds": 300
/// }
/// </code>
/// </summary>
public sealed class SemanticCacheOptions
{
    /// <summary>Configuration section name for IOptions binding.</summary>
    public const string Section = "SemanticCache";

    /// <summary>
    /// Disabled by default. Enable only after the embeddings endpoint
    /// is confirmed working and the cache collection has been created.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Minimum cosine-similarity score for a cached entry to be
    /// returned on lookup. Higher values mean fewer false positives
    /// but lower hit rates.
    ///
    /// Default 0.92 — empirically good for chat completions where the
    /// same intent can be phrased slightly differently. Bump to 0.95
    /// for strict caching (nearly-identical prompts only), lower to
    /// 0.85 for aggressive caching.
    /// </summary>
    public float SimilarityThreshold { get; init; } = 0.92f;

    /// <summary>
    /// Max neighbours to evaluate. The top-N by cosine distance are
    /// returned from Qdrant; the highest-scoring one above threshold
    /// is used. Default 1 means exactly one entry must match.
    /// </summary>
    public uint MaxResults { get; init; } = 1;

    /// <summary>
    /// Default TTL in seconds. Entries expire automatically via the
    /// vector store's point-level TTL. Zero or negative means entries
    /// live until explicitly invalided (not recommended).
    /// </summary>
    public int DefaultTtlSeconds { get; init; } = 300;

    public TimeSpan DefaultTtl => TimeSpan.FromSeconds(DefaultTtlSeconds);

    /// <summary>
    /// Qdrant gRPC endpoint. Defaults to localhost:6334 (Qdrant gRPC
    /// port). Qdrant exposes gRPC on 6334 and REST on 6333.
    /// </summary>
    public string QdrantHost { get; init; } = "localhost";

    /// <summary>
    /// Qdrant gRPC port (default 6334).
    /// </summary>
    public int QdrantPort { get; init; } = 6334;

    /// <summary>
    /// Whether to use HTTPS for the Qdrant connection (default false
    /// for local dev; set true for production Qdrant with TLS).
    /// </summary>
    public bool QdrantUseTls { get; init; }

    /// <summary>
    /// Name of the Qdrant collection holding cached vectors.
    /// </summary>
    public string CollectionName { get; init; } = "semantic_cache";

    /// <summary>
    /// Dimension of the embedding vectors produced by the configured
    /// embedding model. Must match the actual output dimension of the
    /// provider (e.g. 768 for text-embedding-3-small, 1536 for
    /// text-embedding-3-large, 1024 for ada-002).
    ///
    /// Default 768 covers text-embedding-3-small, the most common
    /// OpenAI-compatible embedding model.
    /// </summary>
    public ulong VectorSize { get; init; } = 768;
}
