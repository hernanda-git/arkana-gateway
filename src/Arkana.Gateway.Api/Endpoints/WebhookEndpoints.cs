using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Arkana.Gateway.Api.Endpoints;

/// <summary>
/// Admin API endpoints for managing webhook subscriptions (Phase 5).
/// Webhooks receive event notifications (agent.completed, workflow.completed,
/// token.threshold, error.rate) via HTTP POST with HMAC signature verification.
/// </summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/webhooks").WithTags("Admin");

        group.MapGet("/", async (IWebhookRepository repo) => await ListWebhooks(repo))
            .WithName("ListWebhooks").WithOpenApi();

        group.MapGet("/{id:guid}", async (Guid id, IWebhookRepository repo) => await GetWebhook(id, repo))
            .WithName("GetWebhook").WithOpenApi();

        group.MapPost("/", async (CreateWebhookRequest request, IWebhookRepository repo, HttpContext ctx) => await CreateWebhook(request, repo, ctx))
            .WithName("CreateWebhook").WithOpenApi();

        group.MapPut("/{id:guid}", async (Guid id, UpdateWebhookRequest request, IWebhookRepository repo) => await UpdateWebhook(id, request, repo))
            .WithName("UpdateWebhook").WithOpenApi();

        group.MapDelete("/{id:guid}", async (Guid id, IWebhookRepository repo) => await DeleteWebhook(id, repo))
            .WithName("DeleteWebhook").WithOpenApi();
    }

    // ── Core logic (testable) ─────────────────────────────

    internal static async Task<IResult> ListWebhooks(IWebhookRepository repo)
    {
        var webhooks = await repo.GetAllAsync();
        return Results.Ok(webhooks.Select(w => new
        {
            w.Id,
            w.TenantId,
            w.Url,
            Events = w.GetEvents(),
            w.IsActive,
            w.RetryCount,
            w.CreatedAt,
            w.LastTriggeredAt,
            w.FailureCount
        }));
    }

    internal static async Task<IResult> GetWebhook(Guid id, IWebhookRepository repo)
    {
        var webhook = await repo.GetByIdAsync(id);
        return webhook is not null
            ? Results.Ok(new
            {
                webhook.Id,
                webhook.TenantId,
                webhook.Url,
                Events = webhook.GetEvents(),
                webhook.IsActive,
                webhook.RetryCount,
                webhook.CreatedAt,
                webhook.LastTriggeredAt,
                webhook.FailureCount
            })
            : Results.NotFound(new { message = "Webhook not found." });
    }

    internal static async Task<IResult> CreateWebhook(CreateWebhookRequest request, IWebhookRepository repo, HttpContext ctx)
    {
        var tenantId = ResolveTenantId(ctx, request.TenantId);
        var webhook = Webhook.Create(
            tenantId,
            request.Url,
            request.Secret,
            request.Events ?? [],
            request.RetryCount);

        await repo.CreateAsync(webhook);

        return Results.Created($"/admin/webhooks/{webhook.Id}", new
        {
            webhook.Id,
            webhook.TenantId,
            webhook.Url,
            Events = webhook.GetEvents(),
            webhook.IsActive,
            webhook.RetryCount,
            message = "Webhook created successfully."
        });
    }

    internal static async Task<IResult> UpdateWebhook(Guid id, UpdateWebhookRequest request, IWebhookRepository repo)
    {
        var webhook = await repo.GetByIdAsync(id);
        if (webhook is null)
            return Results.NotFound(new { message = "Webhook not found." });

        if (request.Url is not null)
            webhook.UpdateUrl(request.Url);

        if (request.Secret is not null)
            webhook.UpdateSecret(request.Secret);

        if (request.Events is not null)
            webhook.UpdateEvents(request.Events);

        if (request.RetryCount.HasValue)
            webhook.UpdateRetryCount(request.RetryCount.Value);

        if (request.IsActive.HasValue)
        {
            if (request.IsActive.Value)
                webhook.Activate();
            else
                webhook.Deactivate();
        }

        await repo.UpdateAsync(webhook);

        return Results.Ok(new
        {
            webhook.Id,
            webhook.TenantId,
            webhook.Url,
            Events = webhook.GetEvents(),
            webhook.IsActive,
            webhook.RetryCount,
            webhook.LastTriggeredAt,
            webhook.FailureCount,
            message = "Webhook updated successfully."
        });
    }

    internal static async Task<IResult> DeleteWebhook(Guid id, IWebhookRepository repo)
    {
        var webhook = await repo.GetByIdAsync(id);
        if (webhook is null)
            return Results.NotFound(new { message = "Webhook not found." });
        await repo.DeleteAsync(id);
        return Results.Ok(new { message = "Webhook deleted." });
    }

    /// <summary>
    /// Resolves the tenant for webhook writes. Prefers an explicit tenantId
    /// from the body/query, otherwise falls back to the default admin tenant so
    /// inserts satisfy the TenantId FK constraint.
    /// </summary>
    internal static Guid ResolveTenantId(HttpContext ctx, Guid fromBody)
    {
        if (fromBody != Guid.Empty) return fromBody;
        if (Guid.TryParse(ctx.Request.Query["tenantId"], out var tid) && tid != Guid.Empty)
            return tid;
        return Guid.Parse("00000000-0000-0000-0000-000000000001");
    }
}

/// <summary>Request body for creating a webhook.</summary>
public sealed record CreateWebhookRequest(
    Guid TenantId = default,
    string Url = "",
    string Secret = "",
    string[]? Events = null,
    int RetryCount = 3);

/// <summary>Request body for updating a webhook.</summary>
public sealed record UpdateWebhookRequest(
    string? Url,
    string? Secret,
    string[]? Events,
    int? RetryCount,
    bool? IsActive);
