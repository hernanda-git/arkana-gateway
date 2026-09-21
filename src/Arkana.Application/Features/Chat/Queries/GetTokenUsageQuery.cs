using Arkana.Domain.ValueObjects;
using MediatR;

namespace Arkana.Application.Features.Chat.Queries;

/// <summary>
/// Get token usage statistics for a time period.
/// </summary>
public sealed record GetTokenUsageQuery : IRequest<TokenUsageSummary>
{
    public DateTimeOffset From { get; init; } = DateTimeOffset.UtcNow.AddDays(-7);
    public DateTimeOffset To { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Summary of token usage statistics for a given time period.
/// </summary>
public sealed record TokenUsageSummary
{
    public int TotalRequests { get; init; }
    public int TotalTokens { get; init; }
    public decimal TotalCost { get; init; }
    public IReadOnlyList<ProviderUsage> ByProvider { get; init; } = [];
}

/// <summary>
/// Token usage breakdown grouped by AI provider.
/// </summary>
public sealed record ProviderUsage
{
    public string Provider { get; init; } = string.Empty;
    public int Requests { get; init; }
    public int Tokens { get; init; }
    public decimal Cost { get; init; }
}
