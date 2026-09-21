using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// Admin-facing lifecycle for multi-account ChatGPT / Codex subscriptions.
/// Each connected ChatGPT account is one <see cref="AiProvider"/> row with a
/// stable code <c>chatgpt-accN</c>, bound to the <c>chatgpt</c> OAuth config.
/// The account pool (see <see cref="ChatGptAccountPool"/>) selects among the
/// connected accounts at request time; this service only manages the rows.
///
/// UI-manageable (no env/config needed) per the gateway's operational-state
/// principle: accounts are added/removed from the dashboard, not from appsettings.
/// </summary>
public sealed class ChatGptAccountService
{
    private readonly GatewayDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly IOAuthPendingFlowRepository? _pending;
    private readonly ILogger<ChatGptAccountService> _logger;

    public ChatGptAccountService(
        GatewayDbContext db,
        ITenantProvider tenant,
        ILogger<ChatGptAccountService> logger,
        IOAuthPendingFlowRepository? pending = null)
    {
        _db = db;
        _tenant = tenant;
        _pending = pending;
        _logger = logger;
    }

    private Guid TenantId => _tenant.TenantId
        ?? throw new InvalidOperationException("Authenticated tenant is required.");

    /// <summary>Lists every chatgpt-accN provider with its connection label.</summary>
    public async Task<IReadOnlyList<ChatGptAccountSummary>> ListAsync(CancellationToken ct = default)
    {
        var accounts = await _db.AiProviders
            .Where(p => p.TenantId == TenantId && p.Code.StartsWith("chatgpt-acc"))
            .OrderBy(p => p.Code)
            .ToListAsync(ct);

        var result = new List<ChatGptAccountSummary>();
        foreach (var p in accounts)
        {
            var token = await _db.ProviderOAuthTokens
                .FirstOrDefaultAsync(t => t.TenantId == TenantId && t.AiProviderId == p.Id, ct);
            result.Add(new ChatGptAccountSummary(
                p.Id, p.Code, p.Name, p.IsEnabled,
                token?.Status ?? OAuthTokenStatus.Pending,
                token?.Label));
        }
        return result;
    }

    /// <summary>
    /// Creates the next chatgpt-accN provider (auto-numbered after the highest
    /// existing index). Idempotent-safe: returns the created row's code so the
    /// caller can start its device-code flow against it.
    /// </summary>
    public async Task<ChatGptAccountSummary> CreateAsync(CancellationToken ct = default)
    {
        var max = 0;
        var codes = await _db.AiProviders
            .Where(p => p.TenantId == TenantId && p.Code.StartsWith("chatgpt-acc"))
            .Select(p => p.Code)
            .ToListAsync(ct);
        foreach (var c in codes)
        {
            var tail = c["chatgpt-acc".Length..];
            if (int.TryParse(tail, out var n) && n > max) max = n;
        }

        var next = max + 1;
        var code = $"chatgpt-acc{next}";
        var provider = AiProvider.Create(
            name: $"ChatGPT Account {next}",
            code: code,
            priority: 100 + next,
            baseUrl: "https://chatgpt.com/backend-api",
            costPerInput: 0, costPerOutput: 0);
        // Bound to the chatgpt OAuth config now; the device flow later flips
        // AuthMethod to OAuth once tokens land.
        var cfg = await _db.OAuthProviderConfigs
            .FirstOrDefaultAsync(c => c.ProviderCode == "chatgpt", ct);
        if (cfg is not null)
            provider.SetAuthMethod(AuthMethod.OAuth, cfg.Id);
        provider.AssignTenant(TenantId);

        _db.AiProviders.Add(provider);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created ChatGPT account provider {Code} ({Id}).", code, provider.Id);
        return new ChatGptAccountSummary(provider.Id, code, provider.Name, provider.IsEnabled,
            OAuthTokenStatus.Pending, null);
    }

    /// <summary>Removes a chatgpt-accN account (provider + token + models).</summary>
    public async Task<bool> RemoveAsync(string code, CancellationToken ct = default)
    {
        var provider = await _db.AiProviders
            .FirstOrDefaultAsync(p => p.TenantId == TenantId && p.Code == code, ct);
        if (provider is null) return false;

        if (_pending is not null)
            await _pending.DeletePendingForAccountAsync(code, TenantId, ct);

        var tokens = _db.ProviderOAuthTokens.Where(t => t.TenantId == TenantId && t.AiProviderId == provider.Id);
        _db.ProviderOAuthTokens.RemoveRange(tokens);
        var models = _db.Models.Where(m => m.ProviderId == provider.Id);
        _db.Models.RemoveRange(models);
        _db.AiProviders.Remove(provider);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Removed ChatGPT account provider {Code}.", code);
        return true;
    }

    /// <summary>Toggles an account's enabled state.</summary>
    public async Task<bool> ToggleAsync(string code, CancellationToken ct = default)
    {
        var provider = await _db.AiProviders.FirstOrDefaultAsync(
            p => p.TenantId == TenantId && p.Code == code, ct);
        if (provider is null) return false;
        if (provider.IsEnabled) provider.Disable(); else provider.Enable();
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

public sealed record ChatGptAccountSummary(
    Guid Id, string Code, string Name, bool IsEnabled,
    OAuthTokenStatus Status, string? AccountId);
