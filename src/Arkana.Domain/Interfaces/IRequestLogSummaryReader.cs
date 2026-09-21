namespace Arkana.Domain.Interfaces;

/// <summary>
/// Reads only scalar request-log metadata for dashboards and operational summaries.
/// Implementations must not hydrate JSON message/tool payloads for this path.
/// </summary>
public interface IRequestLogSummaryReader
{
    Task<IReadOnlyList<RequestLogSummary>> SearchSummariesAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? until = null,
        int maxResults = 100,
        CancellationToken ct = default);

    /// <summary>
    /// One page of scalar summaries, newest first, optionally restricted to the
    /// given API-key names (the profile page's "your logs" scope). <c>TotalCount</c>
    /// is the unpaged total for the same filter so callers can render pagination
    /// without loading the whole history.
    /// </summary>
    Task<(IReadOnlyList<RequestLogSummary> Items, int TotalCount)> SearchSummariesPageAsync(
        IReadOnlyCollection<string>? apiKeyNames = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default);
}

public sealed record RequestLogSummary(
    Guid Id,
    string Provider,
    string Model,
    string? ApiKeyName,
    Guid? ResolvedProviderAccountId,
    string? ResolvedProviderAccountCode,
    string RouteKind,
    bool IsError,
    int InputTokens,
    int OutputTokens,
    long DurationTicks,
    DateTimeOffset Timestamp,
    string? ErrorMessage);