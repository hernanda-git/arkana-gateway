using System.Text.Json;
using System.Text.Json.Serialization;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Arkana.Gateway.Api.Endpoints;

/// <summary>
/// Maps POST /v1/embeddings — OpenAI-compatible embedding endpoint.
/// Proxies requests to the upstream provider's /v1/embeddings.
/// </summary>
/// <remarks>AI-ARKANA-006 (Phase 3, task 18).</remarks>
public static class EmbeddingsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void MapEmbeddingsEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapPost("/embeddings", async (HttpContext httpContext,
            IEmbeddingService embeddingService) =>
        {
            var loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("Arkana.Gateway.Api.Embeddings");

            // ── Parse request ─────────────────────────────────
            EmbeddingsRequest? body;
            try
            {
                httpContext.Request.EnableBuffering();
                using var reader = new StreamReader(httpContext.Request.Body, leaveOpen: true);
                var raw = await reader.ReadToEndAsync();
                httpContext.Request.Body.Position = 0;

                if (string.IsNullOrWhiteSpace(raw))
                    return Results.Json(
                        new { error = new { message = "Empty request body", type = "invalid_request_error" } },
                        JsonOpts, statusCode: 400);

                body = JsonSerializer.Deserialize<EmbeddingsRequest>(raw, JsonOpts);
                if (body is null)
                    return Results.Json(
                        new { error = new { message = "Failed to deserialize request body", type = "invalid_request_error" } },
                        JsonOpts, statusCode: 400);
            }
            catch (JsonException)
            {
                logger.LogWarning("[Embeddings] Invalid JSON body.");
                return Results.Json(
                    new { error = new { message = "Invalid JSON body.", type = "invalid_request_error" } },
                    JsonOpts, statusCode: 400);
            }

            if (string.IsNullOrWhiteSpace(body.Model))
                return Results.Json(
                    new { error = new { message = "model is required", type = "invalid_request_error" } },
                    JsonOpts, statusCode: 400);

            // Normalize input to list
            IReadOnlyList<string> inputs;
            if (body.Input is string strInput)
            {
                inputs = new[] { strInput };
            }
            else if (body.Input is JsonElement je)
            {
                inputs = je.ValueKind switch
                {
                    JsonValueKind.String => new[] { je.GetString() ?? "" },
                    JsonValueKind.Array => je.EnumerateArray().Select(e => e.GetString() ?? "").ToList(),
                    _ => new[] { je.ToString() },
                };
            }
            else
            {
                inputs = new[] { body.Input?.ToString() ?? "" };
            }

            if (inputs.Count == 0 || inputs.All(string.IsNullOrWhiteSpace))
                return Results.Json(
                    new { error = new { message = "input must be a non-empty string or array", type = "invalid_request_error" } },
                    JsonOpts, statusCode: 400);

            // ── Generate embeddings ────────────────────────────
            var tenantId = httpContext.RequestServices.GetRequiredService<ITenantProvider>().TenantId;
            if (tenantId is not { } resolvedTenantId)
                return Results.Problem("Authenticated tenant is required.", statusCode: 401);
            var result = await embeddingService.GenerateEmbeddingsAsync(resolvedTenantId, body.Model, inputs, httpContext.RequestAborted);

            if (!result.IsSuccess)
            {
                logger.LogWarning("[Embeddings] Failed for model={Model}: {Error}", body.Model, result.ErrorMessage);
                return Results.Json(
                    new { error = new { message = result.ErrorMessage, type = "upstream_error" } },
                    JsonOpts, statusCode: 502);
            }

            // ── Build response ─────────────────────────────────
            var response = new
            {
                @object = "list",
                data = result.Data.Select(d => new
                {
                    @object = "embedding",
                    index = d.Index,
                    embedding = d.Embedding,
                }).ToList(),
                model = result.Model,
                usage = new
                {
                    prompt_tokens = result.TotalTokens,
                    total_tokens = result.TotalTokens,
                },
            };

            return Results.Json(response, JsonOpts);
        });
    }
}

// ── DTOs ──────────────────────────────────────────────────
internal sealed record EmbeddingsRequest
{
    public string Model { get; init; } = string.Empty;
    public object? Input { get; init; }
}
