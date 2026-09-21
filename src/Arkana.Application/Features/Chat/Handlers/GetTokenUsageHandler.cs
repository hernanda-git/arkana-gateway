using Arkana.Application.Features.Chat.Queries;
using Arkana.Domain.Interfaces;
using MediatR;

namespace Arkana.Application.Features.Chat.Handlers;

/// <summary>
/// Handles token usage queries by retrieving data from the token tracker
/// and aggregating results by provider.
/// </summary>
internal sealed class GetTokenUsageHandler : IRequestHandler<GetTokenUsageQuery, TokenUsageSummary>
{
    private readonly ITokenTracker _tokenTracker;

    public GetTokenUsageHandler(ITokenTracker tokenTracker)
    {
        _tokenTracker = tokenTracker;
    }

    /// <summary>
    /// Handles the token usage query by fetching usage records within the specified date range.
    /// </summary>
    /// <param name="request">The query containing date range parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A summary of token usage, aggregated by provider.</returns>
    public async Task<TokenUsageSummary> Handle(GetTokenUsageQuery request, CancellationToken ct)
    {
        var usage = await _tokenTracker.GetUsageAsync(request.From, request.To, ct);
        var totalCost = await _tokenTracker.GetTotalCostAsync(request.From, request.To, ct);

        var byProvider = usage
            .GroupBy(u => u.Provider)
            .Select(g => new ProviderUsage
            {
                Provider = g.Key,
                Requests = g.Count(),
                Tokens = g.Sum(u => u.TotalTokens),
                Cost = g.Sum(u => u.Cost)
            })
            .ToList();

        return new TokenUsageSummary
        {
            TotalRequests = usage.Count,
            TotalTokens = usage.Sum(u => u.TotalTokens),
            TotalCost = totalCost,
            ByProvider = byProvider
        };
    }
}
