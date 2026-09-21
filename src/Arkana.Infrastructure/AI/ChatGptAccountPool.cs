using System.Collections.Concurrent;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Health-aware selector across the connected ChatGPT/Codex accounts
/// (chatgpt-accN providers). Picks a provider that (a) is enabled, (b) has a
/// connected, non-expired OAuth token, and (c) is not in a 429 cooldown.
///
/// On a 429 from the upstream, the offending account is parked for a short
/// window so subsequent requests spill over to a healthy account instead of
/// hammering the throttled one. Round-robins among the eligible set.
/// </summary>
public sealed class ChatGptAccountPool
{
    private readonly IProviderCatalog _catalog;
    private readonly IOAuthFlowService _oauth;
    private readonly ILogger<ChatGptAccountPool> _logger;

    // account code -> cooldown expiry (UTC). Ephemeral, in-memory; rebuilt
    // from the catalog each time an account recovers.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Cooldowns = new();

    private int _rr;

    public ChatGptAccountPool(
        IProviderCatalog catalog, IOAuthFlowService oauth, ILogger<ChatGptAccountPool> logger)
    {
        _catalog = catalog;
        _oauth = oauth;
        _logger = logger;
    }

    /// <summary>
    /// Selects an account and returns a usable access token. Returns null when
    /// no account is currently available (all throttled / disconnected).
    /// </summary>
    public async Task<ChatGptAccount?> SelectAsync(
        Guid tenantId, string? preferredProviderCode = null, bool allowFallback = false,
        CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            return null;

        var providers = await _catalog.GetAllAsync(tenantId, ct);
        var candidates = providers
            .Where(p => p.Code.StartsWith("chatgpt-acc", StringComparison.OrdinalIgnoreCase)
                        && p.IsEnabled)
            .OrderBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0) return null;

        // A key pin is an ownership boundary. Crossing it requires an explicit
        // per-key fallback opt-in; strict keys fail closed.
        var pin = preferredProviderCode?.Trim();
        if (!string.IsNullOrEmpty(pin))
        {
            var pinned = candidates.FirstOrDefault(p =>
                p.Code.Equals(pin, StringComparison.OrdinalIgnoreCase));
            if (pinned is not null)
            {
                var selected = await TrySelectAsync(pinned, tenantId, ct);
                if (selected is not null)
                {
                    _logger.LogInformation(
                        "ChatGPT account selected: {Code} (requested-by-pin={Pin})",
                        selected.Code, pin);
                    return selected;
                }
            }

            if (!allowFallback)
            {
                _logger.LogWarning("Strict OAuth pin {Pin} unavailable; refusing cross-account fallback", pin);
                return null;
            }
        }

        // Round-robin starting point; try every candidate once.
        var start = (Interlocked.Increment(ref _rr) & int.MaxValue) % candidates.Count;
        for (var i = 0; i < candidates.Count; i++)
        {
            var provider = candidates[(start + i) % candidates.Count];
            if (!string.IsNullOrEmpty(pin) && provider.Code.Equals(pin, StringComparison.OrdinalIgnoreCase))
                continue;

            var selected = await TrySelectAsync(provider, tenantId, ct);
            if (selected is not null)
            {
                if (!string.IsNullOrEmpty(pin))
                    _logger.LogWarning("OAuth pin {Pin} unavailable; fell back to {Actual}", pin, selected.Code);
                _logger.LogInformation(
                    "ChatGPT account selected: {Code} (requested-by-pin={Pin})",
                    selected.Code, pin);
                return selected;
            }
        }

        _logger.LogWarning("ChatGPTAccountPool: no eligible account (all throttled or disconnected).");
        return null;
    }

    private async Task<ChatGptAccount?> TrySelectAsync(AiProvider provider, Guid tenantId, CancellationToken ct)
    {
        if (Cooldowns.TryGetValue(provider.Code, out var until) && until > DateTimeOffset.UtcNow)
            return null;

        var token = await _oauth.GetValidAccessTokenForTenantAsync(provider.Id, tenantId, null, ct);
        if (string.IsNullOrEmpty(token))
            return null;

        // The Codex API requires the per-account ChatGPT-Account-Id header,
        // which is the chatgpt_account_id JWT claim — NOT the provider Code.
        var accountId = provider.AccountId;
        if (string.IsNullOrEmpty(accountId))
            accountId = ExtractChatGptAccountId(token);

        return new ChatGptAccount(provider.Id, provider.Code, token, accountId);
    }

    /// <summary>Decodes the chatgpt_account_id claim from a ChatGPT access token JWT.</summary>
    private static string? ExtractChatGptAccountId(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var pad = parts[1].Length % 4 == 0 ? "" : new string('=', 4 - parts[1].Length % 4);
            var json = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/') + pad));

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Top-level claim (some token shapes)...
            if (root.TryGetProperty("chatgpt_account_id", out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            // ...OpenAI nests it under the https://api.openai.com/auth object.
            if (root.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object &&
                auth.TryGetProperty("chatgpt_account_id", out var aid) && aid.ValueKind == JsonValueKind.String)
                return aid.GetString();
        }
        catch { /* best-effort */ }
        return null;
    }

    /// <summary>Marks an account as throttled; it is skipped until the cooldown lapses.</summary>
    public void MarkThrottled(string code, TimeSpan cooldown)
    {
        Cooldowns[code] = DateTimeOffset.UtcNow.Add(cooldown);
        _logger.LogWarning("ChatGPTAccountPool: account {Code} in 429 cooldown for {Ms}ms.",
            code, cooldown.TotalMilliseconds);
    }

    public static void ClearThrottled(string code) => Cooldowns.TryRemove(code, out _);
}

public sealed record ChatGptAccount(Guid ProviderId, string Code, string AccessToken, string? AccountId);
