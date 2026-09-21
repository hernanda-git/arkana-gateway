using System.Security.Claims;
using Arkana.Gateway.Api.Services;
using Microsoft.AspNetCore.Http;

namespace Arkana.Gateway.Api.Endpoints;

/// <summary>
/// JSON endpoints backing the Google-authenticated /profile page.
///
/// GET /profile/api/usage-windows — latest ChatGPT Codex subscription usage
/// windows (5-hour primary + weekly secondary) per account, scoped to the
/// signed-in user: accounts pinned by the caller's own keys first, plus a
/// non-sensitive pool overview (account code + metrics only — never emails,
/// tokens, or key secrets).
/// </summary>
public static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/profile/api").WithTags("Profile");

        group.MapGet("/usage-windows", async (
            HttpContext http, DashboardService dashboard) =>
        {
            var ownerId = ResolveOwnerUserId(http);
            if (ownerId == Guid.Empty)
                return Results.Unauthorized();

            var view = await dashboard.GetUsageWindowsAsync(ownerId);
            return Results.Ok(view);
        })
        .WithName("GetProfileUsageWindows")
        .RequireAuthorization();

        group.MapGet("/logs", async (
            HttpContext http, DashboardService dashboard,
            int page = 1, int pageSize = 25) =>
        {
            var ownerId = ResolveOwnerUserId(http);
            if (ownerId == Guid.Empty)
                return Results.Unauthorized();

            var (items, total) = await dashboard.GetOwnedLogsPageAsync(ownerId, page, pageSize);
            return Results.Ok(new ProfileLogsPageResponse(
                Page: Math.Max(1, page),
                PageSize: Math.Clamp(pageSize, 5, 200),
                TotalCount: total,
                Items: items));
        })
        .WithName("GetProfileLogsPage")
        .RequireAuthorization();

        group.MapGet("/logs/{id:guid}", async (
            HttpContext http, DashboardService dashboard, Guid id) =>
        {
            var ownerId = ResolveOwnerUserId(http);
            if (ownerId == Guid.Empty)
                return Results.Unauthorized();

            var log = await dashboard.GetOwnedLogByIdAsync(ownerId, id);
            return log is null ? Results.NotFound() : Results.Ok(log);
        })
        .WithName("GetProfileLogDetail")
        .RequireAuthorization();
    }

    /// <summary>Extracts the signed-in dashboard user's id from the auth cookie.</summary>
    internal static Guid ResolveOwnerUserId(HttpContext http)
    {
        var raw = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    /// <summary>Response shape for GET /profile/api/usage-windows.</summary>
    public sealed record UsageWindowsResponse(
        bool HasData,
        IReadOnlyList<UsageWindowDto> Pinned,
        IReadOnlyList<UsageAccountDto> Pool,
        IReadOnlyList<QuotaAccountDto> Accounts)
    {
        /// <summary>At least one pinned account is a ChatGPT Codex account.</summary>
        public bool HasCodex => Accounts.Any(a => a.Kind == "codex");

        /// <summary>At least one pinned account is a Gemini/Antigravity subscription account.</summary>
        public bool HasAntigravity => Accounts.Any(a => a.Kind == "antigravity");
    }

    /// <summary>
    /// One pinned account with the quota state the gateway actually knows about it.
    /// Codex accounts carry rolling usage windows; Antigravity (Gemini subscription)
    /// accounts carry connection/quota-cooldown state because the upstream exposes
    /// no usage-window feed through the broker.
    /// </summary>
    public sealed record QuotaAccountDto(
        string AccountCode,
        string Kind,
        string DisplayName,
        bool IsEnabled,
        string Status,
        DateTimeOffset? QuotaCooldownUntil,
        string? LastFailureClass,
        DateTimeOffset? LastFailureAt,
        DateTimeOffset? LastSuccessAt,
        DateTimeOffset? TokenExpiresAt,
        IReadOnlyList<UsageWindowDto> Windows,
        bool HasSnapshots);

    public sealed record UsageWindowDto(
        string AccountCode,
        string Window,
        double UsedPercent,
        int? WindowMinutes,
        DateTimeOffset? ResetsAtUtc,
        DateTimeOffset UpdatedAtUtc,
        string? PlanType,
        bool IsStale);

    public sealed record UsageAccountDto(
        string AccountCode,
        IReadOnlyList<UsageWindowDto> Windows);

    /// <summary>Response shape for GET /profile/api/logs (paginated, own keys only).</summary>
    public sealed record ProfileLogsPageResponse(
        int Page,
        int PageSize,
        int TotalCount,
        IReadOnlyList<DashboardService.LogSummaryView> Items)
    {
        public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    }
}
