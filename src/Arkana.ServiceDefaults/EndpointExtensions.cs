using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Arkana.ServiceDefaults;

/// <summary>
/// Extension methods for mapping default health check endpoints.
/// </summary>
public static class EndpointExtensions
{
    /// <summary>
    /// Maps standard health check endpoints.
    /// </summary>
    public static IEndpointConventionBuilder MapDefaultEndpoints(this WebApplication app)
    {
        var health = app.MapGroup("");

        health.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }))
              .WithTags("Health");

        health.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }))
              .WithTags("Health");

        return health;
    }
}
