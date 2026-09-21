using Arkana.Domain.ValueObjects;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Tracks token usage for billing and monitoring.
/// </summary>
public interface ITokenTracker
{
    Task RecordUsageAsync(TokenUsage usage, CancellationToken ct = default);
    Task<IReadOnlyList<TokenUsage>> GetUsageAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct = default);
    Task<decimal> GetTotalCostAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct = default);
    Task<IReadOnlyList<TokenUsage>> GetRecentUsageAsync(int count = 50, CancellationToken ct = default);
}
