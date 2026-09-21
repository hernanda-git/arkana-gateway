using Microsoft.AspNetCore.Http;

namespace Arkana.Gateway.Api.Endpoints;

/// <summary>
/// Image generation endpoint — forwards image generation requests to configured providers.
/// Phase 5 kickoff feature.
/// </summary>
public static class ImageGenerationEndpoints
{
    public static void MapImageGenerationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/images").WithTags("Images");

        // POST /v1/images/generations — OpenAI-compatible image generation
        group.MapPost("/generations", async (ImageGenerationRequest request, HttpContext http) =>
            await GenerateImage(request, http))
        .WithName("CreateImageGeneration")
        .WithOpenApi()
        .RequireAuthorization();
    }

    // ── Core logic (testable) ─────────────────────────────

    internal static async Task<IResult> GenerateImage(ImageGenerationRequest request, HttpContext http)
    {
        // Validate request
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return Results.BadRequest(new { error = "Prompt is required." });

        if (request.N < 1 || request.N > 4)
            return Results.BadRequest(new { error = "N must be between 1 and 4." });

        var validSizes = new[] { "256x256", "512x512", "1024x1024", "1024x1792", "1792x1024" };
        if (!validSizes.Contains(request.Size))
            return Results.BadRequest(new { error = $"Invalid size. Supported: {string.Join(", ", validSizes)}." });

        // Extract tenant context from middleware
        var tenantId = http.Items["TenantId"]?.ToString() ?? "anonymous";

        // Forward to configured image provider (stub for Phase 5 kickoff)
        var result = new ImageGenerationResponse
        {
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = Enumerable.Range(0, request.N).Select(i => new ImageData
            {
                Url = $"https://placeholder.example.com/images/{tenantId}/{Guid.NewGuid()}.png",
                RevisedPrompt = request.Prompt
            }).ToList()
        };

        return await Task.FromResult(Results.Ok(result));
    }
}

// ── Request/Response models ─────────────────────────────

public sealed class ImageGenerationRequest
{
    /// <summary>A text description of the desired image.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Model to use (e.g., "dall-e-3", "dall-e-2").</summary>
    public string? Model { get; set; }

    /// <summary>Number of images to generate (1-4).</summary>
    public int N { get; set; } = 1;

    /// <summary>Image size: 256x256, 512x512, 1024x1024, 1024x1792, 1792x1024.</summary>
    public string Size { get; set; } = "1024x1024";

    /// <summary>Response format: "url" or "b64_json".</summary>
    public string? ResponseFormat { get; set; }

    /// <summary>Quality: "standard" or "hd".</summary>
    public string? Quality { get; set; }

    /// <summary>Style: "vivid" or "natural".</summary>
    public string? Style { get; set; }
}

public sealed class ImageGenerationResponse
{
    public long Created { get; set; }
    public List<ImageData> Data { get; set; } = new();
}

public sealed class ImageData
{
    public string? Url { get; set; }
    public string? B64Json { get; set; }
    public string? RevisedPrompt { get; set; }
}
