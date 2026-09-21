using Arkana.Domain.Interfaces;
using Arkana.Domain.ValueObjects;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;

namespace Arkana.Gateway.Api.Middleware;

/// <summary>
/// Middleware that tracks token usage and request duration.
/// </summary>
public sealed class TokenTrackingMiddleware
{
    private readonly RequestDelegate _next;

    public TokenTrackingMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ITokenTracker tracker)
    {
        var sw = Stopwatch.StartNew();

        // Store the stopwatch and tracker in HttpContext.Items for endpoints to use
        context.Items["__sw"] = sw;
        context.Items["__tracker"] = tracker;
        context.Items["__provider"] = "unknown";
        context.Items["__model"] = "unknown";
        context.Items["__inputTokens"] = 0;
        context.Items["__outputTokens"] = 0;
        context.Items["__cost"] = 0m;

        await _next(context);

        sw.Stop();
    }
}
