using Arkana.Application.Features.Admin.Commands;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Gateway.Api.Endpoints;

/// <summary>
/// Maps admin API endpoints for managing API keys, AI providers, logs, and stats.
/// </summary>
public static class AdminEndpoints
{
    /// <summary>
    /// Maps all admin endpoints under the /admin route group.
    /// </summary>
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/admin");

        // ── API Keys ─────────────────────────────────────────

        admin.MapPost("/api-keys", async (CreateApiKeyCommand command, IMediator mediator) =>
        {
            var result = await mediator.Send(command);
            return Results.Created($"/admin/api-keys/{result.Id}", new
            {
                result.Id,
                result.Name,
                command.PreferredProviderCode,
                result.PlainTextKey,
                message = "Save this key securely — it will not be shown again."
            });
        })
        .WithName("CreateApiKey")
        .WithTags("Admin");

        admin.MapGet("/api-keys", async (IApiKeyRepository repo, ITenantProvider tenants) =>
        {
            if (!TryGetTenant(tenants, out var tenantId, out var failure)) return failure;
            var keys = await repo.GetAllAsync(tenantId);
            return Results.Ok(keys.Select(k => new
            {
                k.Id,
                k.Name,
                Prefix = string.IsNullOrEmpty(k.KeyPrefix)
                    ? (k.KeyHash.Length > 12 ? k.KeyHash[..12] + "..." : k.KeyHash[..Math.Min(8, k.KeyHash.Length)] + "...")
                    : k.KeyPrefix + "...",
                k.IsActive,
                k.CreatedAt,
                k.ExpiresAt,
                k.PreferredProviderCode,
                AllowedModelIds = k.AllowedModels.Select(m => m.Id).ToList()
            }));
        })
        .WithName("ListApiKeys")
        .WithTags("Admin");

        admin.MapPut("/api-keys/{id:guid}/toggle", async (Guid id, IApiKeyRepository repo, Infrastructure.Persistence.GatewayDbContext db, ITenantProvider tenants) =>
        {
            if (!TryGetTenant(tenants, out var tenantId, out var failure)) return failure;
            var keys = await repo.GetAllAsync(tenantId);
            var key = keys.FirstOrDefault(k => k.Id == id);
            if (key is null) return Results.NotFound();

            if (key.IsActive)
                key.Deactivate();
            else
                key.Activate();

            // Persist the state change so subsequent authenticated
            // requests honor the deactivation.
            await db.SaveChangesAsync();
            return Results.Ok(new { key.Id, key.IsActive });
        })
        .WithName("ToggleApiKey")
        .WithTags("Admin");

        admin.MapPut("/api-keys/{id:guid}/preferred-provider", async (
            Guid id,
            SetApiKeyPreferredProviderRequest request,
            IApiKeyRepository repo,
            IAiProviderRepository providers,
            ITenantProvider tenants) =>
        {
            if (!TryGetTenant(tenants, out var tenantId, out var failure)) return failure;
            var key = (await repo.GetAllAsync(tenantId)).FirstOrDefault(k => k.Id == id);
            if (key is null) return Results.NotFound();

            if (string.IsNullOrWhiteSpace(request.PreferredProviderCode))
            {
                key.PreferredProviderCode = null;
            }
            else
            {
                var code = request.PreferredProviderCode.Trim();
                var provider = await providers.GetByCodeAsync(code, tenantId);
                if (provider is null || !provider.IsEnabled)
                    return Results.BadRequest(new { error = $"Provider '{code}' does not exist or is disabled." });
                key.PreferredProviderCode = provider.Code;
            }

            await repo.UpdateAsync(key);
            return Results.Ok(new { key.Id, key.PreferredProviderCode });
        })
        .WithName("SetApiKeyPreferredProvider")
        .WithTags("Admin");

        admin.MapDelete("/api-keys/{id:guid}", async (Guid id, IApiKeyRepository repo, Infrastructure.Persistence.GatewayDbContext db, ITenantProvider tenants) =>
        {
            if (!TryGetTenant(tenants, out var tenantId, out var failure)) return failure;
            var keys = await repo.GetAllAsync(tenantId);
            var key = keys.FirstOrDefault(k => k.Id == id);
            if (key is null) return Results.NotFound();

            db.ApiKeys.Remove(key);
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("DeleteApiKey")
        .WithTags("Admin");

        // ── AI Providers ─────────────────────────────────────

        admin.MapGet("/providers", async (IAiProviderRepository repo, ITenantProvider tenants) =>
        {
            if (!TryGetTenant(tenants, out var tenantId, out var failure)) return failure;
            var providers = await repo.GetAllAsync(tenantId);
            return Results.Ok(providers.Select(p => new
            {
                p.Id,
                p.Name,
                p.Code,
                p.BaseUrl,
                p.IsEnabled,
                p.CostPerInputToken,
                p.CostPerOutputToken,
                p.MaxTokensPerRequest,
                p.CreatedAt,
                HasKey = !string.IsNullOrEmpty(p.ApiKey),
                AuthMethod = p.AuthMethod.ToString(),
                OAuthConfigId = p.OAuthConfigId,
                OAuthConfigCode = p.OAuthConfig?.ProviderCode
            }));
        })
        .WithName("ListProviders")
        .WithTags("Admin");

        admin.MapPut("/providers/{id:guid}/toggle", async (Guid id, IAiProviderRepository repo, IProviderCatalog catalog, ITenantProvider tenants) =>
        {
            if (!TryGetTenant(tenants, out var tenantId, out var failure)) return failure;
            var provider = await repo.GetByIdAsync(id, tenantId);
            if (provider is null) return Results.NotFound();

            if (provider.IsEnabled)
                provider.Disable();
            else
                provider.Enable();

            await repo.UpdateAsync(provider);
            // PERF-ARKANA-001: drop the cached snapshot so the next request
            // sees the new enabled state without waiting for the TTL.
            catalog.Invalidate();
            return Results.Ok(new { provider.Id, provider.IsEnabled });
        })
        .WithName("ToggleProvider")
        .WithTags("Admin");

        // ── Provider API Keys ─────────────────────────────────

        // ── Provider auth-method switch (API Key ↔ OAuth) ─────
        admin.MapPut("/providers/{id:guid}/auth-method", async (Guid id, SetProviderAuthMethodRequest req, IAiProviderRepository repo, IProviderCatalog catalog, ITenantProvider tenants) =>
        {
            if (!TryGetTenant(tenants, out var tenantId, out var failure)) return failure;
            var provider = await repo.GetByIdAsync(id, tenantId);
            if (provider is null) return Results.NotFound();

            if (!Enum.TryParse<AuthMethod>(req.AuthMethod, ignoreCase: true, out var method))
                return Results.BadRequest(new { error = $"Unknown AuthMethod '{req.AuthMethod}'." });

            if (method == AuthMethod.OAuth && !req.OAuthConfigId.HasValue)
                return Results.BadRequest(new { error = "OAuthConfigId is required for OAuth auth method." });

            provider.SetAuthMethod(method, req.OAuthConfigId);
            await repo.UpdateAsync(provider);
            catalog.Invalidate();
            return Results.Ok(new { provider.Id, provider.AuthMethod, provider.OAuthConfigId });
        })
        .WithName("SetProviderAuthMethod")
        .WithTags("Admin");

        admin.MapPut("/providers/{id:guid}/apikey", async (Guid id, SetProviderApiKeyRequest req, IAiProviderRepository repo, Arkana.Domain.Services.ICredentialVault vault, IProviderCatalog catalog, ITenantProvider tenants) =>
        {
            if (!TryGetTenant(tenants, out var tenantId, out var failure)) return failure;
            var provider = await repo.GetByIdAsync(id, tenantId);
            if (provider is null) return Results.NotFound();

            // SECURITY: pass the vault to UpdateCredentials so the plaintext is
            // sealed before being written to the database. The plaintext never
            // touches the entity property.
            provider.UpdateCredentials(null, req.ApiKey, vault);
            await repo.UpdateAsync(provider);
            // PERF-ARKANA-001: drop the cached snapshot so the next request
            // picks up the new (sealed) credential without waiting for the TTL.
            catalog.Invalidate();
            return Results.Ok(new { message = $"API key updated for provider '{provider.Name}'" });
        })
        .WithName("SetProviderApiKey")
        .WithTags("Admin");

        // ── ChatGPT / Codex multi-account management ──────────
        // Accounts are UI-manageable (no env/config). Each account is one
        // AiProvider row (chatgpt-accN) bound to the chatgpt OAuth config;
        // the account pool routes requests across the connected ones.

        admin.MapGet("/chatgpt/accounts", async (Arkana.Infrastructure.Services.ChatGptAccountService svc) =>
        {
            var accounts = await svc.ListAsync();
            return Results.Ok(accounts.Select(a => new
            {
                a.Id, a.Code, a.Name, a.IsEnabled,
                Status = a.Status.ToString(),
                a.AccountId
            }));
        })
        .WithName("ListChatGptAccounts")
        .WithTags("Admin");

        admin.MapPost("/chatgpt/accounts", async (Arkana.Infrastructure.Services.ChatGptAccountService svc, IProviderCatalog catalog) =>
        {
            var created = await svc.CreateAsync();
            catalog.Invalidate();
            return Results.Ok(new
            {
                created.Id, created.Code, created.Name,
                Message = $"Account {created.Code} created. Start its device-code link at /oauth/chatgpt/start?code={created.Code}"
            });
        })
        .WithName("CreateChatGptAccount")
        .WithTags("Admin");

        admin.MapDelete("/chatgpt/accounts/{code}", async (string code, Arkana.Infrastructure.Services.ChatGptAccountService svc, IProviderCatalog catalog) =>
        {
            var ok = await svc.RemoveAsync(code);
            catalog.Invalidate();
            return ok ? Results.Ok(new { Message = $"Account {code} removed." })
                      : Results.NotFound(new { Error = $"Account {code} not found." });
        })
        .WithName("RemoveChatGptAccount")
        .WithTags("Admin");

        admin.MapPut("/chatgpt/accounts/{code}/toggle", async (string code, Arkana.Infrastructure.Services.ChatGptAccountService svc, IProviderCatalog catalog) =>
        {
            var ok = await svc.ToggleAsync(code);
            catalog.Invalidate();
            return ok ? Results.Ok(new { Code = code, Message = "Toggled." })
                      : Results.NotFound(new { Error = $"Account {code} not found." });
        })
        .WithName("ToggleChatGptAccount")
        .WithTags("Admin");

        // ── Gemini (CLIProxyAPI) multi-account management ──────
        // Each account is one AiProvider row (gemini-accN) pointing at its own
        // per-account cliproxy container; the connector targets it via the API
        // key's PreferredProviderCode. UI-manageable (no env/config).

        admin.MapGet("/gemini/accounts", async ([FromServices] Arkana.Infrastructure.Services.GeminiAccountService svc) =>
        {
            var accounts = await svc.ListAsync();
            return Results.Ok(accounts.Select(a => new
            {
                a.Id, a.Code, a.Name, a.IsEnabled, a.BaseUrl
            }));
        })
        .WithName("ListGeminiAccounts")
        .WithTags("Admin");

        admin.MapPost("/gemini/accounts", async (
            [FromServices] Arkana.Infrastructure.Services.GeminiAccountService svc,
            [FromServices] IProviderCatalog catalog,
            GeminiAccountCreateRequest? body) =>
        {
            var created = await svc.CreateAsync(body?.CliProxyAlias, default);
            catalog.Invalidate();
            return Results.Ok(new
            {
                created.Id, created.Code, created.Name, created.BaseUrl,
                Message = $"Account {created.Code} created. Stand up its cliproxy container at {created.BaseUrl} and bind an API key to it."
            });
        })
        .WithName("CreateGeminiAccount")
        .WithTags("Admin");

        admin.MapDelete("/gemini/accounts/{code}", async (
            string code,
            [FromServices] Arkana.Infrastructure.Services.GeminiAccountService svc,
            [FromServices] IProviderCatalog catalog) =>
        {
            var ok = await svc.RemoveAsync(code, default);
            catalog.Invalidate();
            return ok ? Results.Ok(new { Message = $"Account {code} removed." })
                      : Results.NotFound(new { Error = $"Account {code} not found." });
        })
        .WithName("RemoveGeminiAccount")
        .WithTags("Admin");

        admin.MapPut("/gemini/accounts/{code}/toggle", async (
            string code,
            [FromServices] Arkana.Infrastructure.Services.GeminiAccountService svc,
            [FromServices] IProviderCatalog catalog) =>
        {
            var ok = await svc.ToggleAsync(code, default);
            catalog.Invalidate();
            return ok ? Results.Ok(new { Code = code, Message = "Toggled." })
                      : Results.NotFound(new { Error = $"Account {code} not found." });
        })
        .WithName("ToggleGeminiAccount")
        .WithTags("Admin");

        // ── Logs ─────────────────────────────────────────────

        admin.MapGet("/logs", async (ITokenTracker tracker, ITenantProvider tenants, int count = 50) =>
        {
            if (!TryGetTenant(tenants, out _, out var failure)) return failure;
            var usages = await tracker.GetRecentUsageAsync(count);
            return Results.Ok(usages.Select(u => new
            {
                u.Provider,
                u.Model,
                u.InputTokens,
                u.OutputTokens,
                u.TotalTokens,
                u.Cost,
                u.Duration,
                u.Timestamp,
                u.ApiKeyName
            }));
        })
        .WithName("GetLogs")
        .WithTags("Admin");

        // ── Stats ────────────────────────────────────────────

        admin.MapGet("/stats", async (IAiProviderRepository providerRepo, IApiKeyRepository keyRepo, ITokenTracker tracker, ITenantProvider tenants) =>
        {
            if (!TryGetTenant(tenants, out var tenantId, out var failure)) return failure;
            var providers = await providerRepo.GetAllAsync(tenantId);
            var keys = await keyRepo.GetAllAsync(tenantId);

            var today = DateTimeOffset.UtcNow.Date;
            var tomorrow = today.AddDays(1);
            var todayUsages = await tracker.GetUsageAsync(today, tomorrow);
            var todayTokens = todayUsages.Sum(u => u.TotalTokens);
            var todayCost = todayUsages.Sum(u => u.Cost);

            return Results.Ok(new
            {
                TotalApiKeys = keys.Count,
                ActiveApiKeys = keys.Count(k => k.IsActive),
                TotalProviders = providers.Count,
                ActiveProviders = providers.Count(p => p.IsEnabled),
                TodayRequests = todayUsages.Count,
                TodayTokens = todayTokens,
                TodayCost = todayCost
            });
        })
        .WithName("GetStats")
        .WithTags("Admin");

        // ── Model Sync (agents use these to refresh provider models) ──

        admin.MapPost("/providers/{id:guid}/sync-models", async (Guid id, IAiProviderRepository repo, IModelRepository models, ICredentialVault vault, IHttpClientFactory http, ITenantProvider tenants) =>
        {
            if (!TryGetTenant(tenants, out var tenantId, out var failure)) return failure;
            var provider = await repo.GetByIdAsync(id, tenantId);
            if (provider is null)
                return Results.NotFound(new { error = "Provider not found" });

            if (string.IsNullOrEmpty(provider.BaseUrl))
                return Results.BadRequest(new { error = "Provider has no BaseUrl configured" });

            var baseUrl = provider.BaseUrl.TrimEnd('/');
            string? apiKey = null;
            try
            {
                apiKey = provider.DecryptApiKey(vault);
            }
            catch
            {
                // Continue without auth if sealed key can't be decrypted
            }

            var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
            if (apiKey is not null)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            HttpResponseMessage resp;
            try
            {
                resp = await client.SendAsync(req);
                resp.EnsureSuccessStatusCode();
            }
            catch (Exception)
            {
                return Results.Problem("Failed to fetch models from upstream.", statusCode: 502);
            }

            var json = await resp.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var dataArr))
                return Results.Ok(new { provider = provider.Name, added = 0, total = 0, message = "No 'data' field in response" });

            var existingModels = await models.GetByProviderIdAsync(id);
            var existingCodes = new HashSet<string>(existingModels.Select(m => m.Code), StringComparer.OrdinalIgnoreCase);

            // Build cost hints from existing models by prefix family
            var costHints = new Dictionary<string, (decimal In, decimal Out)>(StringComparer.OrdinalIgnoreCase)
            {
                ["minimax"] = (0.00000015m, 0.00000060m),
                ["qwen"]    = (0.00000040m, 0.00000160m),
                ["hy3"]     = (0.00000050m, 0.00000200m),
            };
            foreach (var em in existingModels)
            {
                var pfx = em.Code.Split('-', '-', StringSplitOptions.RemoveEmptyEntries)[0];
                if (!costHints.ContainsKey(pfx) && (em.CostPerInputToken > 0 || em.CostPerOutputToken > 0))
                    costHints[pfx] = (em.CostPerInputToken, em.CostPerOutputToken);
            }

            var added = 0;
            var modelList = new List<object>();

            foreach (var item in dataArr.EnumerateArray())
            {
                var modelId = item.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(modelId)) continue;

                var name = string.Join(' ', modelId.Split('-', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Length > 0 ? char.ToUpper(p[0], System.Globalization.CultureInfo.InvariantCulture) + p[1..] : p));

                // Infer costs from model family prefix
                var costIn = 0m; var costOut = 0m;
                foreach (var (pfx, (ci, co)) in costHints)
                {
                    if (modelId.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
                    {
                        costIn = ci; costOut = co;
                        break;
                    }
                }

                if (existingCodes.Contains(modelId))
                {
                    // Update existing zero-cost models with inferred costs
                    var existing = existingModels.FirstOrDefault(m =>
                        m.Code.Equals(modelId, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null && existing.CostPerInputToken == 0 && existing.CostPerOutputToken == 0
                        && (costIn > 0 || costOut > 0))
                    {
                        existing.UpdateDetails(existing.Name, existing.Code, costIn, costOut, existing.MaxTokensPerRequest);
                        await models.UpdateAsync(existing);
                    }
                    continue;
                }

                var model = Arkana.Domain.Entities.Model.Create(id, name, modelId, costIn, costOut, null);
                await models.AddAsync(model);
                added++;
                modelList.Add(new { id = modelId, name });
            }

            return Results.Ok(new
            {
                provider = provider.Name,
                added,
                total = existingModels.Count + added,
                models = modelList
            });
        })
        .WithName("SyncProviderModels")
        .WithTags("Admin");

        admin.MapPost("/providers/sync-all", async (IAiProviderRepository repo, IModelRepository models, ICredentialVault vault, IHttpClientFactory http, ITenantProvider tenants) =>
        {
            if (!TryGetTenant(tenants, out var tenantId, out var failure)) return failure;
            var providers = await repo.GetAllAsync(tenantId);
            var results = new List<object>();
            var totalAdded = 0;

            foreach (var provider in providers.Where(p => p.IsEnabled && !string.IsNullOrEmpty(p.BaseUrl)))
            {
                var baseUrl = provider.BaseUrl!.TrimEnd('/');
                string? apiKey = null;
                try
                {
                    apiKey = provider.DecryptApiKey(vault);
                }
                catch
                {
                    // Continue without auth
                }

                var client = http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);

                var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
                if (apiKey is not null)
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                try
                {
                    var resp = await client.SendAsync(req);
                    resp.EnsureSuccessStatusCode();
                    var json = await resp.Content.ReadAsStringAsync();
                    var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("data", out var dataArr))
                    {
                        results.Add(new { provider = provider.Name, added = 0, error = "No 'data' field" });
                        continue;
                    }

                    var existingModels = await models.GetByProviderIdAsync(provider.Id);
                    var existingCodes = new HashSet<string>(existingModels.Select(m => m.Code), StringComparer.OrdinalIgnoreCase);

                    // Build cost hints from existing models
                    var costHints = new Dictionary<string, (decimal In, decimal Out)>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["minimax"] = (0.00000015m, 0.00000060m),
                        ["qwen"]    = (0.00000040m, 0.00000160m),
                        ["hy3"]     = (0.00000050m, 0.00000200m),
                    };
                    foreach (var em in existingModels)
                    {
                        var pfx = em.Code.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
                        if (!costHints.ContainsKey(pfx) && (em.CostPerInputToken > 0 || em.CostPerOutputToken > 0))
                            costHints[pfx] = (em.CostPerInputToken, em.CostPerOutputToken);
                    }

                    var added = 0;
                    foreach (var item in dataArr.EnumerateArray())
                    {
                        var modelId = item.GetProperty("id").GetString();
                        if (string.IsNullOrEmpty(modelId)) continue;

                        var name = string.Join(' ', modelId.Split('-', StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Length > 0 ? char.ToUpper(p[0], System.Globalization.CultureInfo.InvariantCulture) + p[1..] : p));

                        // Infer costs from model family prefix
                        var costIn = 0m; var costOut = 0m;
                        foreach (var (pfx, (ci, co)) in costHints)
                        {
                            if (modelId.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
                            {
                                costIn = ci; costOut = co;
                                break;
                            }
                        }

                        if (existingCodes.Contains(modelId))
                        {
                            // Update existing zero-cost models
                            var existing = existingModels.FirstOrDefault(m =>
                                m.Code.Equals(modelId, StringComparison.OrdinalIgnoreCase));
                            if (existing is not null && existing.CostPerInputToken == 0 && existing.CostPerOutputToken == 0
                                && (costIn > 0 || costOut > 0))
                            {
                                existing.UpdateDetails(existing.Name, existing.Code, costIn, costOut, existing.MaxTokensPerRequest);
                                await models.UpdateAsync(existing);
                            }
                            continue;
                        }

                        var model = Arkana.Domain.Entities.Model.Create(provider.Id, name, modelId, costIn, costOut, null);
                        await models.AddAsync(model);
                        added++;
                    }

                    totalAdded += added;
                    results.Add(new { provider = provider.Name, added, total = existingModels.Count + added });
                }
                catch (Exception)
                {
                    results.Add(new { provider = provider.Name, added = 0, error = "Provider model synchronization failed." });
                }
            }

            return Results.Ok(new { totalAdded, results });
        })
        .WithName("SyncAllProviderModels")
        .WithTags("Admin");
    }

    private static bool TryGetTenant(ITenantProvider tenants, out Guid tenantId, out IResult failure)
    {
        if (tenants.TenantId is { } resolved && resolved != Guid.Empty)
        {
            tenantId = resolved;
            failure = Results.Ok();
            return true;
        }

        tenantId = Guid.Empty;
        failure = Results.Problem(
            "An authenticated tenant is required for this administrative operation.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
        return false;
    }
}

/// <summary>
/// Request body for creating a Gemini (CLIProxyAPI) account. The optional
/// <see cref="CliProxyAlias"/> is the docker-network alias of this account's
/// cliproxy container; when omitted it defaults to <c>cliproxy-gemini-N</c>.
/// </summary>
public sealed record GeminiAccountCreateRequest(string? CliProxyAlias = null);

/// <summary>Request body for changing or clearing an API-key provider pin.</summary>
public sealed record SetApiKeyPreferredProviderRequest(string? PreferredProviderCode);

/// <summary>Request body for switching a provider's auth method.</summary>
public sealed record SetProviderAuthMethodRequest(string AuthMethod, Guid? OAuthConfigId);

/// <summary>
/// Request body for setting a provider's API key.
/// </summary>
public sealed record SetProviderApiKeyRequest(string ApiKey);
