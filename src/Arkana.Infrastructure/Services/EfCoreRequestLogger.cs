using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// PostgreSQL-backed request logger. Replaces InMemoryRequestLogger.
/// Complex objects (Messages, ToolCalls) are stored as JSONB columns.
/// </summary>
internal sealed class EfCoreRequestLogger : IRequestLogger, IRequestLogSummaryReader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly GatewayDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly IProviderAccountRepository _accounts;

    public EfCoreRequestLogger(GatewayDbContext db, ITenantProvider tenant, IProviderAccountRepository accounts)
    {
        _db = db;
        _tenant = tenant;
        _accounts = accounts;
    }

    private IQueryable<RequestLogEntity> TenantScoped(IQueryable<RequestLogEntity> query)
    {
        var tenantId = _tenant.TenantId ?? throw new InvalidOperationException("Authenticated tenant is required for request logs.");
        return query.Where(l => l.TenantId == tenantId);
    }

    private async Task<RequestLog> ValidateAttributionAsync(RequestLog log, CancellationToken ct)
    {
        var tenantId = _tenant.TenantId ?? throw new InvalidOperationException("Authenticated tenant is required for request logs.");
        if (log.TenantId != tenantId)
            throw new InvalidOperationException("Request log tenant does not match the authenticated tenant.");

        var requested = await ResolveOwnedAccountAsync(tenantId, log.RequestedProviderAccountId, ct);
        var resolved = await ResolveOwnedAccountAsync(tenantId, log.ResolvedProviderAccountId, ct);
        if (log.RequestedProviderAccountId.HasValue && requested is null ||
            log.ResolvedProviderAccountId.HasValue && resolved is null ||
            requested is not null && log.RequestedProviderAccountCode is not null && !string.Equals(requested.Code, log.RequestedProviderAccountCode, StringComparison.OrdinalIgnoreCase) ||
            resolved is not null && log.ResolvedProviderAccountCode is not null && !string.Equals(resolved.Code, log.ResolvedProviderAccountCode, StringComparison.OrdinalIgnoreCase))
            return log with
            {
                RequestedProviderAccountId = null,
                RequestedProviderCode = null,
                RequestedProviderAccountCode = null,
                ResolvedProviderAccountId = null,
                ResolvedProviderAccountCode = null,
                RouteKind = "unattributed"
            };

        return log with
        {
            RequestedProviderCode = log.RequestedProviderCode,
            RequestedProviderAccountCode = requested?.Code ?? log.RequestedProviderAccountCode,
            ResolvedProviderAccountCode = resolved?.Code ?? log.ResolvedProviderAccountCode
        };
    }

    private async Task<ProviderAccount?> ResolveOwnedAccountAsync(Guid tenantId, Guid? accountId, CancellationToken ct)
    {
        if (accountId is not Guid id || id == Guid.Empty)
            return null;
        var account = await _accounts.GetByIdAsync(tenantId, id, ct);
        return account is { IsEnabled: true, DeletedAt: null } ? account : null;
    }

    public async Task RecordAsync(RequestLog log, CancellationToken ct = default)
    {
        log = await ValidateAttributionAsync(log, ct);
        var entity = new RequestLogEntity
        {
            Id = log.Id,
            Provider = log.Provider,
            Model = log.Model,
            ApiKeyName = log.ApiKeyName,
            TenantId = log.TenantId,
            RequestedProviderAccountId = log.RequestedProviderAccountId,
            RequestedProviderCode = log.RequestedProviderCode,
            RequestedProviderAccountCode = log.RequestedProviderAccountCode,
            ResolvedProviderAccountId = log.ResolvedProviderAccountId,
            ResolvedProviderAccountCode = log.ResolvedProviderAccountCode,
            RouteKind = string.IsNullOrWhiteSpace(log.RouteKind) ? "legacy" : log.RouteKind,
            ViaMitmAgent = log.ViaMitmAgent,
            MessagesJson = JsonSerializer.Serialize(log.Messages, JsonOpts),
            ResponseContent = log.ResponseContent,
            ToolCallsJson = log.ToolCalls is { Count: > 0 }
                ? JsonSerializer.Serialize(log.ToolCalls, JsonOpts)
                : null,
            InputTokens = log.InputTokens,
            OutputTokens = log.OutputTokens,
            Cost = log.Cost,
            DurationTicks = log.Duration.Ticks,
            Timestamp = log.Timestamp,
            IsError = log.IsError,
            ErrorMessage = log.ErrorMessage
        };

        _db.RequestLogs.Add(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RequestLog>> GetRecentAsync(
        int count = 100, CancellationToken ct = default)
    {
        var entities = await TenantScoped(_db.RequestLogs)
            .OrderByDescending(l => l.Timestamp)
            .Take(count)
            .ToListAsync(ct);

        return entities.Select(MapToLog).ToList();
    }

    public async Task<RequestLog?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await TenantScoped(_db.RequestLogs)
            .SingleOrDefaultAsync(l => l.Id == id, ct);
        return entity is null ? null : MapToLog(entity);
    }

    public async Task<IReadOnlyList<RequestLogSummary>> SearchSummariesAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? until = null,
        int maxResults = 100,
        CancellationToken ct = default)
    {
        var query = TenantScoped(_db.RequestLogs).AsNoTracking();
        if (from.HasValue)
            query = query.Where(l => l.Timestamp >= from.Value);
        if (until.HasValue)
            query = query.Where(l => l.Timestamp <= until.Value);

        return await query
            .OrderByDescending(l => l.Timestamp)
            .Take(maxResults)
            .Select(l => new RequestLogSummary(
                l.Id, l.Provider, l.Model, l.ApiKeyName, l.ResolvedProviderAccountId, l.ResolvedProviderAccountCode, l.RouteKind,
                l.IsError, l.InputTokens, l.OutputTokens,
                l.DurationTicks, l.Timestamp, l.ErrorMessage))
            .ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<RequestLogSummary> Items, int TotalCount)> SearchSummariesPageAsync(
        IReadOnlyCollection<string>? apiKeyNames = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default)
    {
        var query = TenantScoped(_db.RequestLogs).AsNoTracking();
        if (apiKeyNames is { Count: > 0 })
        {
            var names = apiKeyNames.ToList();
            query = query.Where(l => l.ApiKeyName != null && names.Contains(l.ApiKeyName));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(l => l.Timestamp)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 500))
            .Select(l => new RequestLogSummary(
                l.Id, l.Provider, l.Model, l.ApiKeyName, l.ResolvedProviderAccountId, l.ResolvedProviderAccountCode, l.RouteKind,
                l.IsError, l.InputTokens, l.OutputTokens,
                l.DurationTicks, l.Timestamp, l.ErrorMessage))
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<IReadOnlyList<RequestLog>> SearchAsync(
        string? providerFilter = null,
        string? modelFilter = null,
        string? apiKeyFilter = null,
        string? searchText = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int maxResults = 100,
        CancellationToken ct = default)
    {
        var query = TenantScoped(_db.RequestLogs).AsQueryable();

        if (!string.IsNullOrEmpty(providerFilter) && providerFilter != "*")
            query = query.Where(l => l.Provider == providerFilter);

        if (!string.IsNullOrEmpty(modelFilter) && modelFilter != "*")
            query = query.Where(l => l.Model == modelFilter);

        if (!string.IsNullOrEmpty(apiKeyFilter) && apiKeyFilter != "*")
            query = query.Where(l => l.ApiKeyName != null && l.ApiKeyName == apiKeyFilter);

        if (from.HasValue)
            query = query.Where(l => l.Timestamp >= from.Value);

        if (to.HasValue)
            query = query.Where(l => l.Timestamp <= to.Value);

        // Full-text search across messages JSON, response content, error message, and metadata
        if (!string.IsNullOrEmpty(searchText) && searchText != "*")
        {
            var search = $"%{searchText}%";
            query = query.Where(l =>
                EF.Functions.ILike(l.MessagesJson, search) ||
                EF.Functions.ILike(l.ResponseContent ?? "", search) ||
                EF.Functions.ILike(l.ErrorMessage ?? "", search) ||
                EF.Functions.ILike(l.Provider, search) ||
                EF.Functions.ILike(l.Model, search) ||
                EF.Functions.ILike(l.ApiKeyName ?? "", search));
        }

        var entities = await query
            .OrderByDescending(l => l.Timestamp)
            .Take(maxResults)
            .ToListAsync(ct);

        return entities.Select(MapToLog).ToList();
    }

    private static RequestLog MapToLog(RequestLogEntity e)
    {
        var messages = Deserialize<List<ChatMessage>>(e.MessagesJson) ?? [];
        var toolCalls = !string.IsNullOrEmpty(e.ToolCallsJson)
            ? Deserialize<List<ToolCallInfo>>(e.ToolCallsJson)
            : null;

        return new RequestLog
        {
            Id = e.Id,
            Provider = e.Provider,
            Model = e.Model,
            ApiKeyName = e.ApiKeyName,
            TenantId = e.TenantId,
            RequestedProviderAccountId = e.RequestedProviderAccountId,
            RequestedProviderCode = e.RequestedProviderCode,
            RequestedProviderAccountCode = e.RequestedProviderAccountCode,
            ResolvedProviderAccountId = e.ResolvedProviderAccountId,
            ResolvedProviderAccountCode = e.ResolvedProviderAccountCode,
            RouteKind = e.RouteKind,
            ViaMitmAgent = e.ViaMitmAgent,
            Messages = messages,
            ResponseContent = e.ResponseContent,
            ToolCalls = toolCalls,
            InputTokens = e.InputTokens,
            OutputTokens = e.OutputTokens,
            Cost = e.Cost,
            Duration = new TimeSpan(e.DurationTicks),
            Timestamp = e.Timestamp,
            IsError = e.IsError,
            ErrorMessage = e.ErrorMessage
        };
    }

    private static T? Deserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch { return null; }
    }
}
