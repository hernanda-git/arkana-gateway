using Arkana.Domain.ValueObjects;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Persistent store for full request/response logs including prompts, tool calls, and responses.
/// Separate from ITokenTracker which handles aggregated metrics for the dashboard.
/// </summary>
public interface IRequestLogger
{
    /// <summary>Store a full request log entry.</summary>
    Task RecordAsync(RequestLog log, CancellationToken ct = default);

    /// <summary>Get the most recent N logs.</summary>
    Task<IReadOnlyList<RequestLog>> GetRecentAsync(int count = 100, CancellationToken ct = default);

    /// <summary>Get a single log by its ID.</summary>
    Task<RequestLog?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Search logs with optional filters and full-text search across messages and responses.</summary>
    Task<IReadOnlyList<RequestLog>> SearchAsync(
        string? providerFilter = null,
        string? modelFilter = null,
        string? apiKeyFilter = null,
        string? searchText = null,
        DateTimeOffset? from = null,
        DateTimeOffset? until = null,
        int maxResults = 100,
        CancellationToken ct = default);
}
