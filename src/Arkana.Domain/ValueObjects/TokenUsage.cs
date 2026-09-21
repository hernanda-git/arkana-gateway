namespace Arkana.Domain.ValueObjects;

/// <summary>
/// Immutable token usage record for a single AI request.
/// </summary>
public sealed record TokenUsage
{
    public string Provider { get; init; }
    public string Model { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;
    public decimal Cost { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? ApiKeyName { get; init; }

    /// <summary>Tenant that owns this usage event; empty only for legacy test callers.</summary>
    public Guid TenantId { get; init; }

    public TokenUsage(string provider, string model, int inputTokens, int outputTokens,
        decimal cost, TimeSpan duration, string? apiKeyName = null)
    {
        Provider = provider;
        Model = model;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        Cost = cost;
        Duration = duration;
        ApiKeyName = apiKeyName;
    }
}
