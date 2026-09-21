namespace Arkana.Infrastructure.Persistence.Entities;

/// <summary>
/// EF Core entity for token usage records, mapped to the TokenUsages table.
/// Replaces the in-memory ConcurrentBag in InMemoryTokenTracker.
/// </summary>
public sealed class TokenUsageEntity
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal Cost { get; set; }
    public long DurationTicks { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? ApiKeyName { get; set; }

    // Multi-tenant scoping (ENT-ARKANA-001)
    public Guid TenantId { get; set; }
}
