using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arkana.Gateway.Api.Services;

/// <summary>Applies authenticated request context to endpoint-owned audit rows.</summary>
public static class RequestLogAuditExtensions
{
    /// <summary>
    /// Stamps the ambient authenticated tenant (and the route kind) onto an
    /// endpoint-owned audit row before persisting it.
    ///
    /// This is deliberately the only overload. A previous convenience overload
    /// (<c>RecordWithContextAsync(log, ct)</c>) forwarded the row to
    /// <see cref="IRequestLogger.RecordAsync"/> untouched, so any call site that left
    /// <see cref="RequestLog.TenantId"/> unset handed an empty tenant to
    /// <c>BatchedRequestLogger.ValidateAttributionAsync</c>, which threw from inside the
    /// endpoint and aborted an already-written SSE response (clients observed ECONNRESET /
    /// "socket connection was closed unexpectedly"). Requiring the <see cref="HttpContext"/>
    /// makes the compiler reject any future call site that omits it.
    /// </summary>
    public static Task RecordWithContextAsync(this IRequestLogger logger, RequestLog log,
        HttpContext httpContext, CancellationToken ct = default)
    {
        var tenant = ResolveTenant(httpContext);
        if (log.TenantId != Guid.Empty && log.TenantId != tenant)
            throw new InvalidOperationException("Request log tenant does not match the authenticated tenant.");

        var routeKind = string.IsNullOrWhiteSpace(log.RouteKind) || log.RouteKind == "unknown"
            ? ClassifyRoute(log.Provider)
            : log.RouteKind;

        return logger.RecordAsync(log with { TenantId = tenant, RouteKind = routeKind }, ct);
    }

    /// <summary>
    /// Best-effort variant for response-terminal audit writes. Metering is audit, not
    /// authorization: a failure to write an audit row must never turn a response that is
    /// already on the wire into a broken connection, so the failure is logged and the row is
    /// dropped instead of being rethrown into the request pipeline. A row is still never
    /// persisted with invalid attribution.
    /// </summary>
    public static async Task TryRecordWithContextAsync(this IRequestLogger logger, RequestLog log,
        HttpContext httpContext, CancellationToken ct = default)
    {
        try
        {
            await logger.RecordWithContextAsync(log, httpContext, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Not the caller going away: a cancellation raised inside the write (a scoped CTS, or
            // an upstream timeout surfaced while resolving attribution) still means the row was
            // lost, so it is counted and logged like any other drop.
            ReportDropped(log, httpContext, ex);
        }
        catch (OperationCanceledException)
        {
            // A cancellation that lands while the caller's own token is already cancelled is the
            // expected "client went away" case: stay silent so the dropped-row signal keeps its
            // meaning, and nothing is persisted either way.
            //
            // No endpoint passes a token today — the audit write is a non-blocking enqueue, so rows
            // for aborted-but-completed responses still have to land. This branch is therefore a
            // guard for future callers that DO pass one, not a live path; an OCE raised inside the
            // write (ct not cancelled) is counted as a drop by the catch above instead.
        }
        catch (Exception ex)
        {
            ReportDropped(log, httpContext, ex);
        }
    }

    private static void ReportDropped(RequestLog log, HttpContext httpContext, Exception ex)
    {
        RequestLogAuditMetrics.RecordDropped();
        httpContext.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger("Arkana.Gateway.Api.RequestLogAudit")
            .LogError(ex, "Dropped request-log audit row provider={Provider} model={Model}: {Reason}",
                log.Provider, log.Model, ex.Message);
    }

    private static Guid ResolveTenant(HttpContext httpContext)
        => httpContext.RequestServices.GetRequiredService<ITenantProvider>().TenantId
            ?? throw new InvalidOperationException("Authenticated tenant is required for request logs.");

    private static string ClassifyRoute(string provider)
        => provider.Contains("gemini-acc", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("gemini-subscription", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("cliproxyapi", StringComparison.OrdinalIgnoreCase)
            ? "broker-managed"
            : "direct";
}
