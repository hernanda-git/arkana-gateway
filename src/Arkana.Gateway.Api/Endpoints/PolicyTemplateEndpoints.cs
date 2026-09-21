using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Arkana.Gateway.Api.Endpoints;

/// <summary>
/// Admin API endpoints for managing compliance policy templates.
/// </summary>
public static class PolicyTemplateEndpoints
{
    public static void MapPolicyTemplateEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/policy-templates").WithTags("Admin");

        // GET /admin/policy-templates/list — list all active templates
        group.MapGet("/list", async (IPolicyTemplateRepository repo) => await ListTemplates(repo))
            .WithName("ListPolicyTemplates").WithOpenApi();

        // GET /admin/policy-templates/slug/{slug} — get a single template by slug
        group.MapGet("/slug/{slug}", async (string slug, IPolicyTemplateRepository repo) => await GetBySlug(slug, repo))
            .WithName("GetPolicyTemplate").WithOpenApi();

        group.MapPost("/", async (CreateTemplateRequest request, IPolicyTemplateRepository repo) => await CreateTemplate(request, repo))
            .WithName("CreatePolicyTemplate").WithOpenApi();

        group.MapPut("/{id:guid}", async (Guid id, UpdateTemplateRequest request, IPolicyTemplateRepository repo) => await UpdateTemplate(id, request, repo))
            .WithName("UpdatePolicyTemplate").WithOpenApi();

        group.MapPut("/{id:guid}/deactivate", async (Guid id, IPolicyTemplateRepository repo) => await DeactivateTemplate(id, repo))
            .WithName("DeactivatePolicyTemplate").WithOpenApi();

        group.MapPut("/{id:guid}/activate", async (Guid id, IPolicyTemplateRepository repo) => await ActivateTemplate(id, repo))
            .WithName("ActivatePolicyTemplate").WithOpenApi();

        group.MapDelete("/{id:guid}", async (Guid id, IPolicyTemplateRepository repo) => await DeleteTemplate(id, repo))
            .WithName("DeletePolicyTemplate").WithOpenApi();
    }

    // ── Core logic (testable) ────────────────────────────
    internal static async Task<IResult> ListTemplates(IPolicyTemplateRepository repo)
    {
        var templates = await repo.GetAllAsync();
        return Results.Ok(templates);
    }

    internal static async Task<IResult> GetBySlug(string slug, IPolicyTemplateRepository repo)
    {
        var template = await repo.GetBySlugAsync(slug);
        return template is not null
            ? Results.Ok(template)
            : Results.NotFound(new { message = $"Template '{slug}' not found." });
    }

    internal static async Task<IResult> CreateTemplate(CreateTemplateRequest request, IPolicyTemplateRepository repo)
    {
        var slug = string.IsNullOrWhiteSpace(request.Slug)
            ? request.Name.ToLowerInvariant().Replace(" ", "-")
            : request.Slug!.ToLowerInvariant();

        var existing = await repo.GetBySlugAsync(slug);
        if (existing is not null)
            return Results.Conflict(new { message = $"Template with slug '{slug}' already exists." });

        var template = PolicyTemplate.Create(
            request.Name,
            slug,
            request.Description ?? "",
            request.Config ?? "{}");
        await repo.AddAsync(template);
        return Results.Created($"/admin/policy-templates/slug/{template.Slug}", new
        {
            template.Id,
            template.Name,
            template.Slug,
            message = "Template created successfully."
        });
    }

    internal static async Task<IResult> UpdateTemplate(Guid id, UpdateTemplateRequest request, IPolicyTemplateRepository repo)
    {
        var template = await repo.GetByIdAsync(id);
        if (template is null)
            return Results.NotFound(new { message = "Template not found." });
        if (template.IsBuiltin)
            return Results.BadRequest(new { message = "Cannot modify built-in templates." });
        template.UpdateDetails(
            request.Name ?? template.Name,
            request.Description ?? template.Description,
            request.Config ?? template.Config);
        await repo.UpdateAsync(template);
        return Results.Ok(new { message = "Template updated successfully." });
    }

    internal static async Task<IResult> DeactivateTemplate(Guid id, IPolicyTemplateRepository repo)
    {
        var template = await repo.GetByIdAsync(id);
        if (template is null)
            return Results.NotFound(new { message = "Template not found." });
        template.Deactivate();
        await repo.UpdateAsync(template);
        return Results.Ok(new { message = "Template deactivated." });
    }

    internal static async Task<IResult> ActivateTemplate(Guid id, IPolicyTemplateRepository repo)
    {
        var template = await repo.GetByIdAsync(id);
        if (template is null)
            return Results.NotFound(new { message = "Template not found." });
        template.Activate();
        await repo.UpdateAsync(template);
        return Results.Ok(new { message = "Template activated." });
    }

    internal static async Task<IResult> DeleteTemplate(Guid id, IPolicyTemplateRepository repo)
    {
        var template = await repo.GetByIdAsync(id);
        if (template is null)
            return Results.NotFound(new { message = "Template not found." });
        if (template.IsBuiltin)
            return Results.BadRequest(new { message = "Cannot delete built-in templates." });
        await repo.DeleteAsync(id);
        return Results.Ok(new { message = "Template deleted." });
    }
}

public sealed record CreateTemplateRequest(string Name, string? Slug, string? Description, string? Config);
public sealed record UpdateTemplateRequest(string? Name, string? Description, string? Config);
