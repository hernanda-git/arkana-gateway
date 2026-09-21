using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Gateway.Api.Services;
using Arkana.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace Arkana.Gateway.Api.Endpoints;

/// <summary>
/// Admin API endpoints for managing API key pools (multi-key rotation per provider).
/// </summary>
public static class ApiKeyPoolEndpoints
{
    public static void MapApiKeyPoolEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/key-pools").WithTags("Key Pools");

        // GET /admin/key-pools — list all pools
        group.MapGet("/", async (Guid tenantId, IApiKeyPoolRepository repo) =>
        {
            var pools = await repo.GetAllAsync(tenantId);
            return Results.Ok(pools.Select(p => new
            {
                p.Id,
                p.AiProviderId,
                p.ActiveIndex,
                p.TotalRequests,
                p.TotalRateLimits,
                p.IsActive,
                KeyCount = p.Entries.Count(e => e.IsActive),
                Keys = p.Entries.Select(e => new
                {
                    e.Id,
                    e.Label,
                    e.Priority,
                    e.IsActive,
                    e.IsPermanentlyDisabled,
                    IsInCooldown = e.IsInCooldown,
                    e.LastErrorType,
                    e.RequestCount,
                    e.RateLimitCount,
                    e.ConsecutiveRateLimits,
                    AllowedModels = e.AllowedModels
                }).ToList()
            }));
        })
        .WithName("ListKeyPools")
        .WithOpenApi();

        // GET /admin/key-pools/{providerId} — get pool for a provider
        group.MapGet("/{providerId:guid}", async (Guid providerId, Guid tenantId, IApiKeyPoolRepository repo) =>
        {
            var pool = await repo.GetByProviderAsync(providerId, tenantId);
            return pool is not null ? Results.Ok(pool) : Results.NotFound();
        })
        .WithName("GetKeyPool")
        .WithOpenApi();

        // POST /admin/key-pools — create pool for a provider
        group.MapPost("/", async (CreateKeyPoolRequest req, IApiKeyPoolRepository repo, HttpContext ctx) =>
        {
            var tenantId = ResolveTenantId(ctx, req.TenantId);
            var existing = await repo.GetByProviderAsync(req.AiProviderId, tenantId);
            if (existing != null)
                return Results.Conflict(new { message = "Pool already exists for this provider." });

            var pool = ApiKeyPool.Create(req.AiProviderId, tenantId);
            await repo.CreateAsync(pool);
            return Results.Created($"/admin/key-pools/{pool.Id}", pool);
        })
        .WithName("CreateKeyPool")
        .WithOpenApi();

        // POST /admin/key-pools/{poolId}/keys — add a key to pool
        group.MapPost("/{poolId:guid}/keys", async (Guid poolId, AddKeyRequest req, IApiKeyPoolRepository repo, ICredentialVault vault) =>
        {
            var pool = await repo.GetByIdAsync(poolId);
            if (pool == null) return Results.NotFound();

            var sealedKey = vault.Seal(req.PlaintextKey);
            var entry = ApiKeyPoolEntry.Create(sealedKey!, req.Label, req.Priority, req.CooldownSeconds);
            if (req.AllowedModels is { Length: > 0 })
                entry.AllowedModels = req.AllowedModels;
            pool.Entries.Add(entry);
            await repo.UpdateAsync(pool);

            return Results.Ok(new { entry.Id, entry.Label, entry.Priority });
        })
        .WithName("AddKeyToPool")
        .WithOpenApi();

        // DELETE /admin/key-pools/{poolId}/keys/{entryId} — remove key from pool
        group.MapDelete("/{poolId:guid}/keys/{entryId:guid}", async (Guid poolId, Guid entryId, IApiKeyPoolRepository repo) =>
        {
            var pool = await repo.GetByIdAsync(poolId);
            if (pool == null) return Results.NotFound();

            var entry = pool.Entries.FirstOrDefault(e => e.Id == entryId);
            if (entry == null) return Results.NotFound();

            pool.Entries.Remove(entry);
            await repo.UpdateAsync(pool);
            return Results.Ok(new { message = "Key removed." });
        })
        .WithName("RemoveKeyFromPool")
        .WithOpenApi();

        // PUT /admin/key-pools/{poolId}/keys/{entryId}/deactivate — deactivate key
        group.MapPut("/{poolId:guid}/keys/{entryId:guid}/deactivate", async (Guid poolId, Guid entryId, IApiKeyPoolRepository repo) =>
        {
            var pool = await repo.GetByIdAsync(poolId);
            if (pool == null) return Results.NotFound();

            var entry = pool.Entries.FirstOrDefault(e => e.Id == entryId);
            if (entry == null) return Results.NotFound();

            entry.IsActive = !entry.IsActive;
            await repo.UpdateAsync(pool);
            return Results.Ok(new { entry.Id, entry.IsActive });
        })
        .WithName("ToggleKeyActive")
        .WithOpenApi();

        // PUT /admin/key-pools/{poolId}/keys/{entryId}/models — update allowed models
        group.MapPut("/{poolId:guid}/keys/{entryId:guid}/models", async (Guid poolId, Guid entryId, UpdateModelsRequest req, IApiKeyPoolRepository repo) =>
        {
            var pool = await repo.GetByIdAsync(poolId);
            if (pool == null) return Results.NotFound();

            var entry = pool.Entries.FirstOrDefault(e => e.Id == entryId);
            if (entry == null) return Results.NotFound();

            entry.AllowedModels = req.Models;
            await repo.UpdateAsync(pool);

            return Results.Ok(new { entry.Id, entry.AllowedModels });
        })
        .WithName("UpdateKeyModels")
        .WithOpenApi();

        // POST /admin/key-pools/{poolId}/rotate — manual rotation reset
        group.MapPost("/{poolId:guid}/rotate", async (Guid poolId, IApiKeyPoolRepository repo) =>
        {
            var pool = await repo.GetByIdAsync(poolId);
            if (pool == null) return Results.NotFound();

            pool.ActiveIndex = 0;
            await repo.UpdateAsync(pool);
            return Results.Ok(new { message = "Rotation index reset to 0.", pool.ActiveIndex });
        })
        .WithName("ResetRotation")
        .WithOpenApi();
    }

    /// <summary>
    /// Resolves the tenant for key-pool writes. Prefers an explicit tenantId
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

    /// <summary>Request body for creating a key pool.</summary>
    public sealed record CreateKeyPoolRequest(Guid AiProviderId, Guid TenantId = default);

/// <summary>Request body for adding a key to a pool.</summary>
public sealed record AddKeyRequest(
    string PlaintextKey,
    string Label,
    int Priority = 0,
    int CooldownSeconds = 60,
    string[]? AllowedModels = null);

/// <summary>Request body for updating a key's allowed models.</summary>
public sealed record UpdateModelsRequest(string[]? Models);
}
