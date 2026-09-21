using Arkana.Domain.Entities;
using Arkana.Domain.Services;
using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arkana.Gateway.Api.Services;

/// <summary>
/// Seeds OAuth client templates for the consumer providers we broker: Gemini,
/// OpenAI, Grok (xAI). Employees connect with one click — they never enter a
/// secret. The client id/secret are PLATFORM-HELD (seeded from environment
/// variables, never hard-coded), matching the platform-held credential pattern:
/// are confidential (require a secret from env).
///
/// Idempotent: keyed by ProviderCode. Safe to run on every boot.
/// </summary>
public static class OAuthConfigSeeder
{
    public static async Task SeedAsync(IServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var vault = scope.ServiceProvider.GetRequiredService<ICredentialVault>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("OAuthConfigSeeder");

        await db.Database.MigrateAsync();

        // Note: redirect_uri must be pre-registered in each provider's developer
        // console as {GATEWAY_BASE}/oauth/{code}/callback. GATEWAY_BASE is the
        // public origin (see leaf-cert task); the runtime builds it from the
        // request host, so the registered URI must match what users actually hit.
        var templates = new[]
        {
            // Gemini (Google) — now confidential (client secret from env).
            Make("gemini", "Gemini (Google) OAuth", "https://accounts.google.com/o/oauth2/v2/auth",
                "https://oauth2.googleapis.com/token",
                clientIdEnv: "OAUTH_GEMINI_CLIENT_ID",
                secretEnv: "OAUTH_GEMINI_CLIENT_SECRET",
                scopes: GeminiOAuthScopeContract.Canonical,
                vault: vault),

            // OpenAI — confidential, authorization-code + PKCE.
            Make("openai", "OpenAI OAuth", "https://auth.openai.com/authorize",
                "https://auth.openai.com/oauth/token",
                clientIdEnv: "OAUTH_OPENAI_CLIENT_ID",
                secretEnv: "OAUTH_OPENAI_CLIENT_SECRET",
                scopes: "model.request offline_access",
                vault: vault),

            // Grok (xAI) — confidential, authorization-code + PKCE.
            Make("grok", "Grok (xAI) OAuth", "https://accounts.x.com/oauth2/authorize",
                "https://api.x.com/2/oauth2/token",
                clientIdEnv: "OAUTH_GROK_CLIENT_ID",
                secretEnv: "OAUTH_GROK_CLIENT_SECRET",
                scopes: "model.request offline_access",
                vault: vault),

            // ChatGPT / Codex subscription — OpenAI's first-party Codex OAuth
            // (device-code grant). client_id is OpenAI's public Codex client
            // (same one OpenCode/Hermes hardcode). No secret (public client).
            // Inference + device endpoints are carried in ExtraAuthParams.
            MakeChatGpt(vault),
        };

        foreach (var t in templates)
        {
            var existing = await db.OAuthProviderConfigs
                .FirstOrDefaultAsync(c => c.ProviderCode == t.ProviderCode);
            if (existing is null)
            {
                db.OAuthProviderConfigs.Add(t);
                existing = t;
                logger.LogInformation("Seeded OAuth template '{Code}'.", t.ProviderCode);
            }
            else if (!string.IsNullOrEmpty(t.ClientId) && (existing.ClientId != t.ClientId || existing.SealedClientSecret != t.SealedClientSecret))
            {
                var updateVault = scope.ServiceProvider.GetRequiredService<ICredentialVault>();
                var plainSecret = t.DecryptClientSecret(updateVault);
                existing.UpdateEndpoints(
                    t.AuthorizationEndpoint, t.TokenEndpoint, t.DeviceAuthorizationEndpoint,
                    t.ClientId, t.Scopes, t.ExtraAuthParams,
                    clientSecretPlaintext: plainSecret, vault: updateVault);
                logger.LogInformation("Updated OAuth template '{Code}' credentials.", t.ProviderCode);
            }

            if (t.ProviderCode == "gemini" && existing!.Scopes != GeminiOAuthScopeContract.Canonical)
            {
                // Controlled, idempotent remediation of legacy scope drift. This
                // changes only the Gemini template's scopes and never credentials.
                existing.UpdateScopes(GeminiOAuthScopeContract.Canonical);
                logger.LogInformation("Reconciled Gemini OAuth scope contract.");
            }
        }

        await db.SaveChangesAsync();
    }

    private static OAuthProviderConfig Make(
        string code, string display, string authorize, string tokenEndpoint,
        string? clientIdEnv = null, string? secretEnv = null, string? scopes = null,
        ICredentialVault? vault = null, string? extraAuthParams = null)
    {
        var clientId = clientIdEnv is { } e ? Environment.GetEnvironmentVariable(e) ?? "" : "";
        var secret = secretEnv is { } s ? Environment.GetEnvironmentVariable(s) : null;
        return OAuthProviderConfig.Create(code, display, OAuthGrant.AuthorizationCode,
            tokenEndpoint, clientId,
            authorizationEndpoint: authorize,
            clientSecretPlaintext: secret, vault: vault, scopes: scopes,
            extraAuthParams: extraAuthParams);
    }

    /// <summary>
    /// ChatGPT / Codex subscription OAuth template. Uses OpenAI's first-party
    /// public Codex client_id (env-overridable) and the device-code grant.
    /// Inference + device endpoints live in <see cref="ChatGptEndpoints"/>.
    /// </summary>
    private static OAuthProviderConfig MakeChatGpt(ICredentialVault? vault)
    {
        var clientId = Environment.GetEnvironmentVariable("OAUTH_CHATGPT_CLIENT_ID")
            ?? "app_EMoamEEZ73f0CkXaXp7hrann"; // OpenAI's first-party Codex client
        var endpoints = new ChatGptEndpoints();
        return OAuthProviderConfig.Create("chatgpt", "ChatGPT (Codex) OAuth",
            OAuthGrant.DeviceCode,
            "https://auth.openai.com/oauth/token",
            clientId,
            authorizationEndpoint: "https://auth.openai.com/oauth/authorize",
            clientSecretPlaintext: null, vault: vault,
            scopes: "openid profile email offline_access",
            extraAuthParams: endpoints.ToJson());
    }
}
