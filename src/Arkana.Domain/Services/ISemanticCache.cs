using Arkana.Domain.Interfaces;

namespace Arkana.Domain.Services;

/// <summary>
/// Semantic (vector-similarity) cache for chat completions.
///
/// Unlike <see cref="IResponseCache"/> (exact-match), this cache can
/// return a cached response for a request that is semantically similar
/// — but not identical — to a previously-cached one. It works by:
///
///   1. Embedding the chat request (messages + model) via the existing
///      <see cref="IEmbeddingService"/>.
///   2. Searching a vector index (Qdrant) for the nearest neighbors
///      above a configurable cosine-similarity threshold.
///   3. Returning the cached response payload on a hit; storing it on
///      a miss + successful upstream completion.
///
/// Design constraints (AI-ARKANA-005):
///   - Cache writes/reads are **never** on the critical path for the
///     chat response. Failures are caught, logged, and swallowed so
///     the caller always gets the upstream response.
///   - TTL is managed by the vector store (point-level TTL in Qdrant)
///     so there is no background eviction pass.
///   - The embedding model used for indexing must be the same one used
///     for querying. If the embedding model changes, call
///     <see cref="InvalidateAllAsync"/> so old embeddings don't
///     produce false-similarity matches.
/// </summary>
public interface ISemanticCache
{
    /// <summary>Total cache hits since process start (for metrics).</summary>
    long Hits { get; }

    /// <summary>Total cache misses since process start (for metrics).</summary>
    long Misses { get; }

    /// <summary>
    /// Look up a semantically similar cached response.
    /// Returns null on miss, error, or TTL expiry.
    /// </summary>
    /// <param name="chatRequest">The canonicalized chat request.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CachedResponse?> GetSimilarAsync(ChatRequest chatRequest, CancellationToken ct = default);

    /// <summary>
    /// Store a chat response in the semantic cache.
    /// The request is embedded and the vector upserted alongside the
    /// serialized <see cref="CachedResponse"/> payload.
    /// </summary>
    Task StoreAsync(ChatRequest chatRequest, CachedResponse response, CancellationToken ct = default);

    /// <summary>
    /// Drop every entry in the semantic cache. Called on admin actions
    /// that change provider configuration, embedding model, or any
    /// global state that could make cached responses stale.
    /// </summary>
    Task InvalidateAllAsync(CancellationToken ct = default);
}
