using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Microsoft.AspNetCore.Http;

namespace Arkana.Gateway.Api.Middleware;

/// <summary>
/// Authenticates requests via API key (X-Api-Key header) or JWT Bearer token.
/// For Phase 1, validates against stored hashed API keys.
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;

    public ApiKeyAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IApiKeyRepository repo)
    {
        // Skip auth for health endpoints
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        // The /admin management API is authenticated by AdminAuthMiddleware,
        // which understands the two credentials that surface accepts (a
        // dashboard Admin session or ADMIN_API_KEY). It is NOT unauthenticated:
        // this middleware validates tenant API keys, which admin callers do not
        // present, so it defers rather than rejecting them here.
        if (context.Request.Path.StartsWithSegments("/admin"))
        {
            await _next(context);
            return;
        }

        if (!ApiKeyExtractor.TryExtract(context.Request, out var apiKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "API key required" });
            return;
        }

        var hash = ApiKeyHasher.Hash(apiKey);
        var key = await repo.GetByKeyHashAsync(hash, context.RequestAborted);

        if (key is null || !key.IsActive || key.IsExpired())
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired API key" });
            return;
        }

        // Store key info for downstream usage
        context.Items["ApiKeyId"] = key.Id;
        context.Items["ApiKeyName"] = key.Name;
        context.Items["TenantId"] = key.TenantId;
        context.Items["ApiKeyModelIds"] = key.AllowedModels.Select(m => m.Id).ToArray();
        context.Items["ApiKeyPreferredProvider"] = key.PreferredProviderCode;
        context.Items["ApiKeyPreferredProviderAccountId"] = key.PreferredProviderAccountId;
        context.Items["ApiKeyAllowProviderFallback"] = key.AllowProviderFallback;

        await _next(context);
    }
}
