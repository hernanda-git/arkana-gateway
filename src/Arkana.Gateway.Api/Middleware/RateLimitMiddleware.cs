using Arkana.Domain.Services;
using Microsoft.AspNetCore.Http;

namespace Arkana.Gateway.Api.Middleware;

/// <summary>
/// Per-API-key rate limiting for chat / completion endpoints
/// (SEC-ARKANA-005). Runs AFTER <see cref="ApiKeyAuthMiddleware"/>
/// so the validated <c>ApiKeyName</c> is in
/// <see cref="HttpContext.Items"/>.
///
/// Scope: only /v1/* endpoints. Admin/dashboard/health/auth
/// paths are exempt — they have their own (coarser) access
/// controls.
///
/// What this middleware does NOT do: TPM accounting. TPM is
/// charged post-flight by the chat handler (we only know the
/// token count after the upstream responds). The pre-flight
/// check here covers RPM and concurrency; TPM overage is
/// logged-and-accepted for the current response, with the
/// overage carried into the next minute's window.
/// </summary>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;

    public RateLimitMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IRateLimiter limiter)
    {
        // Only enforce on the public chat surface.
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Auth middleware stored the validated name here. If the
        // request reached us without a name it means either:
        //   (a) auth didn't run (misconfiguration) — let it pass
        //       and the handler will return 401 itself, or
        //   (b) the path is one auth exempts (e.g. /v1/models).
        if (!context.Items.TryGetValue("ApiKeyName", out var nameObj)
            || nameObj is not string apiKeyName
            || string.IsNullOrEmpty(apiKeyName))
        {
            await _next(context);
            return;
        }

        var lease = await limiter.TryAcquireSlotAsync(apiKeyName, context.RequestAborted);
        if (lease is null)
        {
            // Rejected. We don't carry the specific reason (RPM
            // vs concurrency) in the response body to keep the
            // surface small — both produce 429.
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = "60";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "rate_limit_exceeded",
                message = $"API key '{apiKeyName}' has hit its rate limit. Retry after 60s."
            });
            return;
        }

        // Stash the lease on the context so the chat handler can
        // charge TPM post-flight and dispose the lease at the end.
        // (We can't use `await using` here because the handler
        // runs in the same async scope but a different frame.)
        context.Items["RateLimitLease"] = lease;
        try
        {
            await _next(context);
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }
}
