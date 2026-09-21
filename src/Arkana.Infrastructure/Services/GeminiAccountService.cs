using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// Admin-facing lifecycle for multi-account Gemini subscriptions backed by
/// CLIProxyAPI (Antigravity OAuth -> Google One AI Pro).
///
/// Each connected Gemini account is one <see cref="AiProvider"/> row with a
/// stable code <c>gemini-accN</c>, pointing at its own cliproxy container
/// (one container per Google account; CLIProxyAPI v7 has no per-request
/// account selector). The <see cref="CLIProxyAPIChatService"/> connector targets
/// the exact account via <see cref="ChatRequest.PreferredProviderCode"/>, which
/// the API key pins — so a key bound to <c>gemini-acc2</c> reaches only that
/// account. This mirrors the ChatGPT / Codex subscription model.
///
/// UI-manageable (no env/config needed): accounts are added/removed from the
/// dashboard, not from appsettings. The cliproxy container itself is stood up
/// out-of-band (one <c>docker run</c> per account, distinct docker-network
/// alias), then the matching <c>gemini-accN</c> row is created here.
/// </summary>
public sealed class GeminiAccountService
{
    private readonly GatewayDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly ILogger<GeminiAccountService> _logger;

    // Models served by a Gemini Pro subscription through CLIProxyAPI.
    // Both Gemini and Claude come from the same Antigravity entitlement.
    private static readonly (string Name, string Code)[] AccountModels =
    [
        ("Gemini 3 Flash", "gemini-3-flash"),
        ("Gemini 3.6 Flash High", "gemini-3.6-flash-high"),
        ("Gemini 3.7 Flash High", "gemini-3.7-flash-high"),
        ("Gemini 3.1 Pro Low", "gemini-3.1-pro-low"),
        ("Gemini Pro Agent", "gemini-pro-agent"),
        ("Claude Sonnet 4.6", "claude-sonnet-4-6"),
    ];

    public GeminiAccountService(GatewayDbContext db, ITenantProvider tenant, ILogger<GeminiAccountService> logger)
    {
        _db = db;
        _tenant = tenant;
        _logger = logger;
    }

    private Guid TenantId => _tenant.TenantId
        ?? throw new InvalidOperationException("Authenticated tenant is required.");

    /// <summary>Lists every gemini-accN provider with its enabled state.</summary>
    public async Task<IReadOnlyList<GeminiAccountSummary>> ListAsync(CancellationToken ct = default)
    {
        var accounts = await _db.AiProviders
            .Where(p => p.TenantId == TenantId && p.Code.StartsWith("gemini-acc"))
            .OrderBy(p => p.Code)
            .ToListAsync(ct);

        return accounts.Select(p => new GeminiAccountSummary(
            p.Id, p.Code, p.Name, p.IsEnabled, p.BaseUrl ?? string.Empty)).ToList();
    }

    /// <summary>
    /// Creates the next gemini-accN provider (auto-numbered after the highest
    /// existing index) bound to a cliproxy container on the given
    /// docker-network alias. Idempotent-safe: returns the new row's code.
    /// </summary>
    /// <param name="cliproxyAlias">
    /// Docker-network alias / host of this account's cliproxy container.
    /// Defaults to <c>cliproxy-gemini-N</c>.
    /// </param>
    public async Task<GeminiAccountSummary> CreateAsync(
        string? cliproxyAlias = null, CancellationToken ct = default)
    {
        var max = 0;
        var codes = await _db.AiProviders
            .Where(p => p.TenantId == TenantId && p.Code.StartsWith("gemini-acc"))
            .Select(p => p.Code)
            .ToListAsync(ct);
        foreach (var c in codes)
        {
            var tail = c["gemini-acc".Length..];
            if (int.TryParse(tail, out var n) && n > max) max = n;
        }

        var next = max + 1;
        var code = $"gemini-acc{next}";
        var alias = cliproxyAlias?.Trim()
                    ?? $"cliproxy-gemini-{next}";
        var baseUrl = $"http://{alias}:8317";

        var provider = AiProvider.Create(
            name: $"Gemini Account {next}",
            code: code,
            priority: 100 + next,
            baseUrl: baseUrl,
            costPerInput: 0, costPerOutput: 0);
        // Auth is the local cliproxy key, sealed from env/config at runtime;
        // the connector falls back to CLIPROXYAPI_API_KEY when the DB key is null.
        provider.SetAuthMethod(AuthMethod.ApiKey);
        provider.AssignTenant(TenantId);

        _db.AiProviders.Add(provider);
        await _db.SaveChangesAsync(ct);

        // Seed the model rows this account exposes.
        foreach (var (name, modelCode) in AccountModels)
        {
            _db.Models.Add(Model.Create(provider.Id, name, modelCode));
        }
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created Gemini account provider {Code} ({Id}) -> {BaseUrl}.", code, provider.Id, baseUrl);
        return new GeminiAccountSummary(provider.Id, code, provider.Name, provider.IsEnabled, baseUrl);
    }

    /// <summary>Removes a gemini-accN account (provider + its model rows).</summary>
    public async Task<bool> RemoveAsync(string code, CancellationToken ct = default)
    {
        var provider = await _db.AiProviders
            .FirstOrDefaultAsync(p => p.TenantId == TenantId && p.Code == code, ct);
        if (provider is null) return false;

        var models = _db.Models.Where(m => m.ProviderId == provider.Id);
        _db.Models.RemoveRange(models);
        _db.AiProviders.Remove(provider);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Removed Gemini account provider {Code}.", code);
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

public sealed record GeminiAccountSummary(
    Guid Id, string Code, string Name, bool IsEnabled, string BaseUrl);
