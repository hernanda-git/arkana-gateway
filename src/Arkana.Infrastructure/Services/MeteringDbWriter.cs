using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Arkana.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// Persists a batch of <see cref="MeteringEvent"/>s to PostgreSQL
/// using a single <c>SaveChangesAsync</c> per kind
/// (PERF-ARKANA-005).
///
/// Why two SaveChanges round-trips (one for usage, one for logs)
/// instead of one? The two tables have no FK relationship, so a
/// single round-trip would require EF to wrap them in a
/// transaction, which is more expensive than the two-INSERT
/// batched path on PostgreSQL. The two-round-trip cost is still
/// O(1) per flush tick instead of O(N) per chat completion.
/// </summary>
public sealed class MeteringDbWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IDbContextFactory<GatewayDbContext> _dbFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeteringDbWriter> _logger;

    // SLA metrics recorder (Phase 6). We resolve it from a scope
    // inside WriteAsync so this singleton writer never holds a
    // scoped DbContext. Recording runs best-effort and never blocks
    // the request-log insert above.

    public MeteringDbWriter(
        IDbContextFactory<GatewayDbContext> dbFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<MeteringDbWriter> logger)
    {
        _dbFactory = dbFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Persist a batch of events. Returns the count of events
    /// written. On failure, throws — the caller (the flusher) is
    /// responsible for logging the failure and recording the
    /// counter increment.
    /// </summary>
    public async Task<int> WriteAsync(IReadOnlyList<MeteringEvent> batch, CancellationToken ct = default)
    {
        if (batch.Count == 0) return 0;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var usageEntities = new List<TokenUsageEntity>(batch.Count);
        var logEntities = new List<RequestLogEntity>(batch.Count);

        foreach (var evt in batch)
        {
            switch (evt)
            {
                case MeteringEvent.Usage u:
                    usageEntities.Add(MapUsage(u.Value));
                    break;
                case MeteringEvent.LogEntry l:
                    logEntities.Add(MapLog(l.Value));
                    break;
            }
        }

        // Use a transaction so the two INSERTs are atomic. If the
        // request log write fails after the usage write succeeded
        // (or vice versa), we want to roll back rather than leave
        // the dashboard seeing mismatched counts.
        await using IDbContextTransaction tx =
            await db.Database.BeginTransactionAsync(ct);

        if (usageEntities.Count > 0)
        {
            db.TokenUsages.AddRange(usageEntities);
        }
        if (logEntities.Count > 0)
        {
            db.RequestLogs.AddRange(logEntities);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // ── SLA metrics (Phase 6) ──────────────────────────────
        // Recorded OUTSIDE the metering transaction so a transient
        // SLA write failure can never roll back or block the
        // request-log insert above. SLA is best-effort observability;
        // the audit log is the source of truth. We open a fresh
        // scope here because this writer is a singleton and the
        // recorder/context are scoped.
        try
        {
            // The well-known default tenant — the only tenant that
            // exists in this single-tenant deployment. RequestLogEntity
            // does not carry a tenant (MapLog omits it), so SLA metrics
            // are scoped to the default tenant rather than the all-zero
            // GUID that would violate the SlaMetrics->Tenants FK.
            const string DefaultTenantId = "00000000-0000-0000-0000-000000000001";
            var tenantId = Guid.Parse(DefaultTenantId);

            await using var slaScope = _scopeFactory.CreateAsyncScope();
            var recorder = slaScope.ServiceProvider
                .GetService<ISlaMetricsRecorder>();
            if (recorder is not null)
            {
                foreach (var l in logEntities)
                {
                    var latencyMs = new TimeSpan(l.DurationTicks).TotalMilliseconds;
                    if (l.IsError)
                        await recorder.RecordFailureAsync(
                            l.Provider, l.Model, latencyMs, tenantId, ct);
                    else
                        await recorder.RecordSuccessAsync(
                            l.Provider, l.Model, latencyMs, tenantId, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SLA metric recording failed (non-fatal)");
        }

        _logger.LogDebug(
            "Metering flush: wrote {Usage} usage + {Log} log rows",
            usageEntities.Count,
            logEntities.Count);

        return batch.Count;
    }

    private static TokenUsageEntity MapUsage(TokenUsage u) => new()
    {
        Id = Guid.NewGuid(),
        Provider = u.Provider,
        Model = u.Model,
        InputTokens = u.InputTokens,
        OutputTokens = u.OutputTokens,
        Cost = u.Cost,
        DurationTicks = u.Duration.Ticks,
        Timestamp = u.Timestamp.ToUniversalTime(),
        ApiKeyName = u.ApiKeyName,
        TenantId = u.TenantId
    };

    private static RequestLogEntity MapLog(RequestLog log) => new()
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
        RouteKind = log.RouteKind,
        ViaMitmAgent = log.ViaMitmAgent,
        MessagesJson = JsonSerializer.Serialize(log.Messages, JsonOpts),
        ToolCallsJson = log.ToolCalls is { Count: > 0 }
            ? JsonSerializer.Serialize(log.ToolCalls, JsonOpts)
            : null,
        ResponseContent = log.ResponseContent,
        InputTokens = log.InputTokens,
        OutputTokens = log.OutputTokens,
        Cost = log.Cost,
        DurationTicks = log.Duration.Ticks,
        Timestamp = log.Timestamp,
        IsError = log.IsError,
        ErrorMessage = log.ErrorMessage,
    };
}
