namespace Arkana.Domain.Interfaces;

/// <summary>
/// Generates vector embeddings for text inputs via registered providers.
/// Follows the OpenAI /v1/embeddings contract shape.
/// </summary>
/// <remarks>AI-ARKANA-006 (Phase 3, task 18) — Embeddings endpoint.</remarks>
public interface IEmbeddingService
{
    /// <summary>Provider identifier (e.g., "opencode", "openai").</summary>
    string ProviderName { get; }

    /// <summary>
    /// Generate embeddings for the given inputs.
    /// </summary>
    /// <param name="model">Embedding model name (e.g., "text-embedding-3-small").</param>
    /// <param name="inputs">One or more input strings to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Embedding result with vectors and usage metadata.</returns>
    Task<EmbeddingResult> GenerateEmbeddingsAsync(
        Guid tenantId,
        string model,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default);
}

public sealed record EmbeddingResult
{
    public IReadOnlyList<EmbeddingVector> Data { get; init; } = [];
    public string Model { get; init; } = string.Empty;
    public int TotalTokens { get; init; }
    public bool IsSuccess { get; init; } = true;
    public string? ErrorMessage { get; init; }
}

public sealed record EmbeddingVector
{
    public int Index { get; init; }
    public IReadOnlyList<float> Embedding { get; init; } = [];
}
