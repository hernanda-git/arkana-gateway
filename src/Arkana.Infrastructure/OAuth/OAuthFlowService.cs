using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.OAuth;

/// <summary>
/// Server-orchestrated OAuth: the gateway builds the provider
/// authorize URL (PKCE + state), the browser redirects to the provider, the
/// provider redirects back to the gateway's callback, and the gateway exchanges
/// the code for tokens server-side (sealed into <see cref="ProviderOAuthToken"/>).
/// Employees never supply a secret — client id/secret are platform-held (seeded
/// from env). The matching <see cref="AiProvider"/> (by Code) is flipped to OAuth
/// on success so the connector can resolve the live bearer.
/// </summary>
public sealed class OAuthFlowService : IOAuthFlowService, IDisposable
{
    private readonly IProviderOAuthTokenRepository _tokens;
    private readonly IOAuthProviderConfigRepository _configs;
    private readonly IOAuthPendingFlowRepository _pending;
    private readonly IAiProviderRepository _providers;
    private readonly ICredentialVault _vault;
    private readonly ITenantProvider _tenant;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<OAuthFlowService> _log;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshGates = new(StringComparer.Ordinal);

    public OAuthFlowService(
        IProviderOAuthTokenRepository tokens,
        IOAuthProviderConfigRepository configs,
        IOAuthPendingFlowRepository pending,
        IAiProviderRepository providers,
        ICredentialVault vault,
        ITenantProvider tenant,
        IHttpClientFactory http,
        ILogger<OAuthFlowService> log,
        IServiceScopeFactory? scopeFactory = null)
    {
        _tokens = tokens;
        _configs = configs;
        _pending = pending;
        _providers = providers;
        _vault = vault;
        _tenant = tenant;
        _http = http;
        _log = log;
        _scopeFactory = scopeFactory;
    }

    public async Task<OAuthStartResult> StartAsync(string providerCode, string redirectBase, CancellationToken ct = default)
    {
        // Try to resolve AiProvider by code (multi-account: "gemini-acc1").
        // If found and linked to an OAuth config, use that config.
        // Fallback: look up OAuth config directly by code (original single-account flow).
        var provider = await _providers.GetByCodeAsync(providerCode, CurrentTenant(), ct);
        OAuthProviderConfig? cfg = null;
        string configCode;
        if (provider?.OAuthConfigId is not null)
        {
            cfg = await _configs.GetByIdAsync(provider.OAuthConfigId.Value, ct);
            configCode = cfg?.ProviderCode ?? providerCode;
        }
        else
        {
            cfg = await _configs.GetByCodeAsync(providerCode, ct);
            configCode = providerCode;
        }
        if (cfg is null)
            throw new InvalidOperationException($"No OAuth config for '{providerCode}'.");

        // Device-code grant (e.g. ChatGPT/Codex): the provider does NOT support
        // an authorization-code redirect; instead the user enters a short code at
        // a verification URL. Branch early and return device specifics.
        if (cfg.GrantType == OAuthGrant.DeviceCode)
        {
            if (provider is null)
                throw new InvalidOperationException($"No AiProvider with code '{providerCode}' to start a device flow.");
            var device = await StartChatGptDeviceAsync(provider.Code, ct);
            if (!device.Success)
                throw new InvalidOperationException(device.Error ?? "Device flow could not start.");
            return new OAuthStartResult(cfg.ProviderCode, string.Empty, device.State ?? string.Empty, cfg.DisplayName,
                IsDeviceCode: true, UserCode: device.UserCode, VerificationUrl: device.VerificationUrl);
        }

        await ClearExpiredPendingAsync();

        var state = Guid.NewGuid().ToString("N");
        var (verifier, challenge) = MakePkce();
        // Always use the CONFIG code in the redirect URI (matches registered Google URI)
        var redirectUri = $"{redirectBase.TrimEnd('/')}/oauth/{configCode}/callback";

        var authUrl = BuildAuthUrl(cfg, state, challenge, redirectUri);

        var tenantId = CurrentTenant();
        var targetProviderCode = provider?.Code; // null for single-account fallback
        var flow = OAuthPendingFlow.Create(tenantId, configCode, state, verifier, redirectUri, _vault, TimeSpan.FromMinutes(10), aiProviderCode: targetProviderCode);
        await _pending.AddAsync(flow, ct);

        return new OAuthStartResult(configCode, authUrl, state, cfg.DisplayName);
    }

    public async Task<OAuthCallbackResult> HandleCallbackAsync(string providerCode, string code, string state, CancellationToken ct = default)
    {
        if (!IsSafeOAuthState(state) || !IsSafeOAuthValue(code, 8192))
            return new OAuthCallbackResult(OAuthTokenStatus.Error, "Invalid OAuth callback parameters.");

        var cfg = await _configs.GetByCodeAsync(providerCode, ct)
            ?? throw new InvalidOperationException($"No OAuth config for '{providerCode}'.");

        // The callback is intentionally anonymous. The state is bound to the
        // stored tenant/provider/account and is claimed atomically below.
        var flow = await _pending.GetByStateAsync(state, null, ct);
        if (flow is null || flow.IsExpired
            || !string.Equals(flow.ProviderCode, providerCode, StringComparison.OrdinalIgnoreCase))
            return new OAuthCallbackResult(OAuthTokenStatus.Error, "Unknown, expired, or mismatched OAuth state.");
        if (flow.HasCompletionFailure)
            return new OAuthCallbackResult(OAuthTokenStatus.Error, "OAuth completion failed; restart the connection.");

        var claimedAt = DateTimeOffset.UtcNow;
        if (!await _pending.TryClaimAsync(flow.Id, flow.TenantId, claimedAt, ct))
        {
            // A retry after the external exchange is safe only when the sealed
            // token response was durably recorded. Never send the one-time code
            // to the provider a second time.
            var targetCode = flow.AiProviderCode ?? providerCode;
            var existingProvider = await ResolveProviderAsync(targetCode, ct, flow.TenantId);
            if (existingProvider is not null)
            {
                var linkedToken = await FindCompletionTokenAsync(flow, existingProvider, ct);
                if (linkedToken is not null)
                    return await FinalizePersistedCompletionAsync(flow, existingProvider, cfg, linkedToken, false, ct);
                if (flow.HasCompletionTokens)
                    return await FinalizePendingCompletionAsync(flow, existingProvider, cfg, false, ct);
            }

            return new OAuthCallbackResult(
                OAuthTokenStatus.Error,
                flow.HasCompletionTokens
                    ? "OAuth completion is waiting for local finalization; retry recovery."
                    : "OAuth state is already being processed or was already used.");
        }

        var exchangeCompleted = false;
        AiProvider? completionProvider = null;
        var completionTokenDurable = false;
        try
        {
            var verifier = flow.DecryptCodeVerifier(_vault);
            if (string.IsNullOrWhiteSpace(verifier))
                return await ReleaseAndReturnAsync(flow, flow.TenantId, claimedAt, "OAuth state is invalid.");

            var targetCode = flow.AiProviderCode ?? providerCode;
            var provider = await ResolveProviderAsync(targetCode, ct, flow.TenantId);
            if (provider is null)
                return await ReleaseAndReturnAsync(flow, flow.TenantId, claimedAt, "OAuth provider is unavailable.");
            completionProvider = provider;

            var bodyFields = new Dictionary<string, string>
            {
                ["client_id"] = cfg.ClientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = flow.RedirectUri,
            };
            if (!string.IsNullOrEmpty(cfg.SealedClientSecret))
                bodyFields["client_secret"] = cfg.DecryptClientSecret(_vault)!;
            using var body = new FormUrlEncodedContent(bodyFields);

            var client = _http.CreateClient("oauth");
            using var resp = await client.PostAsync(cfg.TokenEndpoint, body, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return await ReleaseAndReturnAsync(flow, flow.TenantId, claimedAt, "Token exchange failed.");

            // The provider may have consumed the code once it returned a success
            // response. From this point onward the claim is never released.
            exchangeCompleted = true;
            var doc = JsonSerializer.Deserialize<JsonElement>(raw, _json);
            var access = doc.GetString("access_token");
            if (string.IsNullOrEmpty(access))
                return await MarkCompletionFailureAsync(
                    flow,
                    flow.TenantId,
                    claimedAt,
                    deviceCompletionClaim: false,
                    "OAuth provider returned an incomplete token response.");

            var refresh = doc.GetString("refresh_token");
            var tokenType = doc.GetString("token_type") ?? "Bearer";
            var expiresIn = doc.GetInt32OrDefault("expires_in");
            var refreshExpiresIn = doc.GetInt32OrDefault("refresh_token_expires_in");
            var expiresAt = expiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresIn) : (DateTimeOffset?)null;
            var refreshExpiresAt = refreshExpiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(refreshExpiresIn) : (DateTimeOffset?)null;

            flow.StoreCompletionTokens(access, refresh, tokenType, expiresAt, refreshExpiresAt, _vault);
            // Seal the completion state before writing the provider token row. If
            // either write fails after the external exchange, recovery has at
            // least one durable copy and never needs to replay the code.
            await _pending.UpdateAsync(flow, ct);
            var completionToken = await PersistCompletionTokenAsync(flow, provider, ct);
            completionTokenDurable = true;
            return await FinalizePersistedCompletionAsync(flow, provider, cfg, completionToken, false, ct);
        }
        catch (OperationCanceledException)
        {
            if (exchangeCompleted)
            {
                if (!flow.HasCompletionTokens)
                    await MarkCompletionFailureAsync(
                        flow,
                        flow.TenantId,
                        claimedAt,
                        deviceCompletionClaim: false,
                        "OAuth provider returned an unusable token response.");
                else
                    await TryPersistCompletionTokenForRecoveryAsync(flow, completionProvider, completionTokenDurable);
            }
            else
                await _pending.TryReleaseClaimAsync(flow.Id, flow.TenantId, claimedAt, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            if (exchangeCompleted)
            {
                if (!flow.HasCompletionTokens)
                    return await MarkCompletionFailureAsync(
                        flow,
                        flow.TenantId,
                        claimedAt,
                        deviceCompletionClaim: false,
                        "OAuth provider returned an unusable token response.");
                await TryPersistCompletionTokenForRecoveryAsync(flow, completionProvider, completionTokenDurable);
            }
            else
                await _pending.TryReleaseClaimAsync(flow.Id, flow.TenantId, claimedAt, CancellationToken.None);
            _log.LogWarning("OAuth callback exchange failed with {ExceptionType}.", ex.GetType().Name);
            return new OAuthCallbackResult(
                OAuthTokenStatus.Error,
                exchangeCompleted
                    ? "OAuth exchange completed; local finalization is pending. Retry the callback or recovery endpoint."
                    : "OAuth callback exchange failed.");
        }
    }

    public async Task<OAuthCallbackResult> RecoverCallbackAsync(string providerCode, string state, CancellationToken ct = default)
    {
        if (!IsSafeOAuthState(state))
            return new OAuthCallbackResult(OAuthTokenStatus.Error, "OAuth recovery state is invalid.");

        var cfg = await _configs.GetByCodeAsync(providerCode, ct)
            ?? throw new InvalidOperationException($"No OAuth config for '{providerCode}'.");
        var flow = await _pending.GetByStateAsync(state, null, ct);
        if (flow is null || flow.IsExpired
            || !string.Equals(flow.ProviderCode, providerCode, StringComparison.OrdinalIgnoreCase))
            return new OAuthCallbackResult(OAuthTokenStatus.Error, "Unknown or mismatched OAuth recovery state.");
        if (flow.HasCompletionFailure)
            return new OAuthCallbackResult(OAuthTokenStatus.Error, "OAuth completion failed; restart the connection.");
        var targetCode = flow.AiProviderCode ?? providerCode;
        var provider = await ResolveProviderAsync(targetCode, ct, flow.TenantId);
        if (provider is null)
            return new OAuthCallbackResult(OAuthTokenStatus.Error, "OAuth provider is unavailable.");

        var linkedToken = await FindCompletionTokenAsync(flow, provider, ct);
        if (linkedToken is not null)
            return await FinalizePersistedCompletionAsync(flow, provider, cfg, linkedToken, false, ct);
        if (!flow.HasCompletionTokens)
            return new OAuthCallbackResult(OAuthTokenStatus.Error, "No durable OAuth completion is available for recovery.");

        return await FinalizePendingCompletionAsync(flow, provider, cfg, false, ct);
    }

    private async Task<ProviderOAuthToken?> FindCompletionTokenAsync(
        OAuthPendingFlow flow,
        AiProvider provider,
        CancellationToken ct)
    {
        var token = await _tokens.GetByProviderAsync(provider.Id, flow.TenantId, ct);
        return token?.CompletionFlowId == flow.Id ? token : null;
    }

    private async Task<ProviderOAuthToken> PersistCompletionTokenAsync(
        OAuthPendingFlow flow,
        AiProvider provider,
        CancellationToken ct)
    {
        var access = flow.DecryptCompletionAccessToken(_vault);
        if (string.IsNullOrWhiteSpace(access))
            throw new InvalidOperationException("No durable OAuth completion is available for recovery.");

        var refresh = flow.DecryptCompletionRefreshToken(_vault);
        var existing = await _tokens.GetByProviderAsync(provider.Id, flow.TenantId, ct);
        var row = existing ?? ProviderOAuthToken.CreatePending(provider.Id, flow.TenantId);
        row.StoreTokens(
            _vault.Seal(access)!,
            refresh is null ? null : _vault.Seal(refresh),
            flow.CompletionTokenType ?? "Bearer",
            flow.CompletionExpiresAt,
            flow.CompletionRefreshExpiresAt);
        row.BindCompletionFlow(flow.Id);
        if (existing is null)
            await _tokens.AddAsync(row, ct);
        else
            await _tokens.UpdateAsync(row, ct);
        return row;
    }

    private async Task<OAuthCallbackResult> FinalizePendingCompletionAsync(
        OAuthPendingFlow flow,
        AiProvider provider,
        OAuthProviderConfig cfg,
        bool includeChatGptAccountId,
        CancellationToken ct)
    {
        var row = await PersistCompletionTokenAsync(flow, provider, ct);
        return await FinalizePersistedCompletionAsync(flow, provider, cfg, row, includeChatGptAccountId, ct);
    }

    private async Task<OAuthCallbackResult> FinalizePersistedCompletionAsync(
        OAuthPendingFlow flow,
        AiProvider provider,
        OAuthProviderConfig cfg,
        ProviderOAuthToken row,
        bool includeChatGptAccountId,
        CancellationToken ct)
    {
        var access = row.DecryptAccessToken(_vault);
        if (string.IsNullOrWhiteSpace(access))
            return new OAuthCallbackResult(OAuthTokenStatus.Error, "No durable OAuth completion is available for recovery.");

        var providerChanged = false;
        if (!provider.UsesOAuth || provider.OAuthConfigId != cfg.Id)
        {
            provider.SetAuthMethod(AuthMethod.OAuth, cfg.Id);
            providerChanged = true;
        }

        if (includeChatGptAccountId)
        {
            var accountId = ExtractChatGptAccountId(access);
            if (!string.IsNullOrWhiteSpace(accountId))
            {
                provider.SetAccountId(accountId);
                providerChanged = true;
            }
        }

        if (providerChanged)
            await _providers.UpdateAsync(provider, flow.TenantId, ct);

        await _pending.DeleteAsync(flow.Id, flow.TenantId, ct);
        return new OAuthCallbackResult(OAuthTokenStatus.Connected);
    }


    public async Task<OAuthConnectionStatus> GetStatusAsync(string providerCode, CancellationToken ct = default)
    {
        var provider = await ResolveProviderAsync(providerCode, ct);
        if (provider is null)
            return new OAuthConnectionStatus(providerCode, OAuthTokenStatus.Pending, null, false, "No provider configured.");
        var token = await _tokens.GetByProviderAsync(provider.Id, CurrentTenant(), ct);
        return new OAuthConnectionStatus(
            providerCode,
            token?.Status ?? OAuthTokenStatus.Pending,
            token?.ExpiresAt,
            token is { SealedRefreshToken: not null },
            token?.LastError);
    }

    public async Task DisconnectAsync(string providerCode, CancellationToken ct = default)
    {
        var provider = await ResolveProviderAsync(providerCode, ct);
        if (provider is not null)
        {
            var token = await _tokens.GetByProviderAsync(provider.Id, CurrentTenant(), ct);
            if (token is not null) { token.Revoke(); await _tokens.UpdateAsync(token, ct); }
            if (provider.UsesOAuth)
            {
                provider.SetAuthMethod(AuthMethod.ApiKey);
                await _providers.UpdateAsync(provider, CurrentTenant(), ct);
            }
        }
        await ClearExpiredPendingAsync();
    }

    public Task<string?> GetValidAccessTokenAsync(Guid providerId, TimeSpan? refreshSlack = null, CancellationToken ct = default)
        => GetValidAccessTokenCoreAsync(providerId, CurrentTenant(), refreshSlack, ct);

    public Task<string?> GetValidAccessTokenForTenantAsync(Guid providerId, Guid tenantId, TimeSpan? refreshSlack = null, CancellationToken ct = default)
        => GetValidAccessTokenCoreAsync(providerId, tenantId, refreshSlack, ct);

    private async Task<string?> GetValidAccessTokenCoreAsync(Guid providerId, Guid tenantId, TimeSpan? refreshSlack, CancellationToken ct)
    {
        var token = await _tokens.GetByProviderAsync(providerId, tenantId, ct);
        if (token is null || token.Status != OAuthTokenStatus.Connected) return null;

        var slack = refreshSlack ?? TimeSpan.FromMinutes(5);
        if (token.NeedsRefresh(slack))
        {
            var gate = RefreshGates.GetOrAdd($"{tenantId:N}:{providerId:N}", _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                // Re-read after acquiring the gate so a concurrent request or
                // refresher can win without rotating the refresh token twice.
                token = await _tokens.GetByProviderAsync(providerId, tenantId, ct);
                if (token is null || token.Status != OAuthTokenStatus.Connected)
                    return null;
                if (token.NeedsRefresh(slack))
                {
                    token = await TryRefreshAsync(token, ct);
                    if (token is null)
                        return null;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        if (token.Status != OAuthTokenStatus.Connected || token.ExpiresAt < DateTimeOffset.UtcNow)
            return null;

        return token.DecryptAccessToken(_vault);
    }

    // ── helpers ──────────────────────────────────────────────

    private async Task<AiProvider?> ResolveProviderAsync(string providerCode, CancellationToken ct, Guid? tenantId = null)
    {
        var provider = await _providers.GetByCodeAsync(providerCode, tenantId ?? CurrentTenant(), ct);
        return provider;
    }

    private Guid CurrentTenant()
        => _tenant.TenantId ?? throw new InvalidOperationException("Authenticated tenant is required.");

    private static Task ClearExpiredPendingAsync()
    {
        // Best-effort: expiry is checked at callback time (HandleCallbackAsync rejects
        // expired state). Old rows are unique per state and harmless; a future bulk
        // sweep can prune them. No-op today.
        return Task.CompletedTask;
    }

    private async Task<ProviderOAuthToken?> TryRefreshAsync(ProviderOAuthToken token, CancellationToken ct)
    {
        try
        {
            var provider = token.Provider ?? await _providers.GetByIdAsync(token.AiProviderId, token.TenantId, ct);
            if (provider?.OAuthConfigId is not { } configId) return token;
            var cfg = await _configs.GetByIdAsync(configId, ct);
            if (cfg is null || string.IsNullOrEmpty(token.SealedRefreshToken)) return token;

            var refresh = token.DecryptRefreshToken(_vault);
            if (string.IsNullOrEmpty(refresh)) return token;

            var bodyFields = new Dictionary<string, string>
            {
                ["client_id"] = cfg.ClientId,
                ["refresh_token"] = refresh,
                ["grant_type"] = "refresh_token",
            };
            if (!string.IsNullOrEmpty(cfg.SealedClientSecret))
                bodyFields["client_secret"] = cfg.DecryptClientSecret(_vault)!;
            using var body = new FormUrlEncodedContent(bodyFields);

            var client = _http.CreateClient("oauth");
            using var resp = await client.PostAsync(cfg.TokenEndpoint, body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode is HttpStatusCode.BadRequest
                    or HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden)
                    token.MarkExpired();
                else
                    token.MarkTransientError("OAuth refresh is temporarily unavailable.");
                return await PersistRefreshStateAsync(token, ct);
            }

            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(ct), _json);
            var access = doc.GetString("access_token");
            if (string.IsNullOrEmpty(access))
            {
                token.MarkTransientError("OAuth refresh returned no usable credential.");
                return await PersistRefreshStateAsync(token, ct);
            }

            var newRefresh = doc.GetString("refresh_token") ?? refresh;
            var expiresIn = doc.GetInt32OrDefault("expires_in");
            var expiresAt = expiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresIn) : token.ExpiresAt;
            token.RefreshTokens(_vault.Seal(access)!, _vault.Seal(newRefresh), doc.GetString("token_type") ?? "Bearer", expiresAt, token.RefreshExpiresAt);
            return await PersistRefreshStateAsync(token, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning("OAuth refresh failed with {ExceptionType} for provider {Provider}.",
                ex.GetType().Name, token.AiProviderId);
            token.MarkTransientError("OAuth refresh is temporarily unavailable.");
            return await PersistRefreshStateAsync(token, CancellationToken.None);
        }
    }

    private async Task<ProviderOAuthToken?> PersistRefreshStateAsync(ProviderOAuthToken token, CancellationToken ct)
    {
        try
        {
            await _tokens.UpdateAsync(token, ct);
            return token;
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            // Another gateway instance won the rotation. Do not query through
            // this context: the conflicted loser entity may still be tracked
            // with its locally rotated credential. A fresh scope forces EF to
            // read the committed winner row from the database.
            _log.LogDebug("OAuth refresh state changed concurrently for provider {Provider}.", token.AiProviderId);
            if (_scopeFactory is null)
                return null;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var freshTokens = scope.ServiceProvider.GetRequiredService<IProviderOAuthTokenRepository>();
            return await freshTokens.GetByProviderAsync(
                token.AiProviderId, token.TenantId, CancellationToken.None);
        }
    }

    private static string BuildAuthUrl(OAuthProviderConfig cfg, string state, string challenge, string redirectUri)
    {
        var ub = new StringBuilder(cfg.AuthorizationEndpoint);
        ub.Append("?response_type=code");
        ub.Append("&client_id=").Append(Uri.EscapeDataString(cfg.ClientId));
        ub.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        ub.Append("&scope=").Append(Uri.EscapeDataString(cfg.Scopes ?? ""));
        ub.Append("&state=").Append(state);
        ub.Append("&code_challenge=").Append(challenge);
        ub.Append("&code_challenge_method=S256");
        return ub.ToString();
    }

    private static (string Verifier, string Challenge) MakePkce()
    {
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        var bytes = new byte[32];
        rng.GetBytes(bytes);
        var verifier = Base64Url(bytes);
        var challenge = Base64Url(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => GC.SuppressFinalize(this);
// ── ChatGPT / Codex device-code flow ────────────────────────────
// OpenAI's first-party Codex OAuth. The gateway is a server (no localhost
// browser), so we use the device-authorization grant: the admin is shown a
// user_code to enter at auth.openai.com/codex/device; the gateway polls the
// device token endpoint until OpenAI returns an authorization_code +
// code_verifier, then exchanges it at the token endpoint. No client secret
// (public client). Mirrors OpenCode/Hermes behavior (same client_id).

public async Task<ChatGptDeviceStartResult> StartChatGptDeviceAsync(string aiProviderCode, CancellationToken ct = default)
{
    var cfg = await _configs.GetByCodeAsync("chatgpt", ct)
        ?? throw new InvalidOperationException("No OAuth config for 'chatgpt'.");
    var eps = cfg.ChatGptEndpoints ?? new ChatGptEndpoints();
    var provider = await ResolveProviderAsync(aiProviderCode, ct);
    if (provider is null)
        throw new InvalidOperationException($"No AiProvider with code '{aiProviderCode}'.");

    var client = _http.CreateClient("oauth");
    using var startReq = new HttpRequestMessage(HttpMethod.Post, eps.DeviceUsercodeUrl)
    {
        Content = JsonContent.Create(new { client_id = cfg.ClientId }),
    };
    var req = await client.SendAsync(startReq, ct);
    var raw = await req.Content.ReadAsStringAsync(ct);
    if (!req.IsSuccessStatusCode)
    {
        _log.LogWarning("ChatGPT device auth start failed with HTTP {StatusCode}", (int)req.StatusCode);
        return new ChatGptDeviceStartResult(false, null, null, "Device auth start failed.");
    }
    var doc = JsonSerializer.Deserialize<JsonElement>(raw, _json);
    var deviceAuthId = doc.GetString("device_auth_id");
    var userCode = doc.GetString("user_code");
    if (string.IsNullOrEmpty(deviceAuthId) || string.IsNullOrEmpty(userCode))
        return new ChatGptDeviceStartResult(false, null, null, "Device auth response missing ids.");

    var (verifier, challenge) = MakePkce();
    var tenantId = CurrentTenant();
    // Drop any prior device flows for this account so a stale (un-authorized or
    // expired) session can't trap the poll loop. The user must authorize the
    // single code we show next.
    await _pending.DeletePendingForAccountAsync(aiProviderCode, CurrentTenant(), ct);
    var flow = OAuthPendingFlow.Create(tenantId, "chatgpt", Guid.NewGuid().ToString("N"),
        verifier, $"{eps.DeviceTokenUrl}", _vault, TimeSpan.FromMinutes(10),
        aiProviderCode: aiProviderCode, deviceAuthId: deviceAuthId, deviceUserCode: userCode);
    await _pending.AddAsync(flow, ct);

    return new ChatGptDeviceStartResult(true, userCode,
        "https://auth.openai.com/codex/device", null, flow.State);
}

/// <summary>
/// Polls the device token endpoint. Returns a result once OpenAI yields the
/// authorization_code + code_verifier (or an error). The caller then calls
/// <see cref="CompleteChatGptDeviceAsync"/> to exchange + store.
/// </summary>
public async Task<ChatGptDevicePollResult> PollChatGptDeviceAsync(string state, CancellationToken ct = default)
{
    var tenantId = CurrentTenant();
    var flow = await _pending.GetByStateAsync(state, tenantId, ct);
    if (flow is null || flow.IsExpired)
        return new ChatGptDevicePollResult(false, "unknown", null, "Unknown or expired device flow.");
    if (string.IsNullOrEmpty(flow.DeviceAuthId))
        return new ChatGptDevicePollResult(false, "error", null, "Flow is not a device-code flow.");
    if (!string.IsNullOrEmpty(flow.DecryptAuthorizationCode(_vault))
        && !string.IsNullOrEmpty(flow.DecryptCodeVerifier(_vault)))
        return new ChatGptDevicePollResult(true, "authorized", state, null);
    if (flow.ClaimedAt is not null)
        return new ChatGptDevicePollResult(false, "error", state,
            "Device authorization is awaiting recovery; restart linking if completion does not resume.");

    var cfg = await _configs.GetByCodeAsync("chatgpt", ct)
        ?? throw new InvalidOperationException("No OAuth config for 'chatgpt'.");
    var eps = cfg.ChatGptEndpoints ?? new ChatGptEndpoints();
    var client = _http.CreateClient("oauth");
    var pollClaimedAt = DateTimeOffset.UtcNow;
    if (!await _pending.TryClaimAsync(flow.Id, tenantId, pollClaimedAt, ct))
        return new ChatGptDevicePollResult(false, "error", state, "Device poll is already being completed.");

    var authorizationReceived = false;
    try
    {
        using var pollReq = new HttpRequestMessage(HttpMethod.Post, eps.DeviceTokenUrl)
        {
            Content = JsonContent.Create(new
            {
                device_auth_id = flow.DeviceAuthId,
                user_code = flow.DeviceUserCode ?? "",
            }),
        };
        var resp = await client.SendAsync(pollReq, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        // Stale/expired device session: OpenAI no longer knows this device_auth_id.
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var notFound = TryGetNestedErrorCode(raw);
            await _pending.TryReleaseClaimAsync(flow.Id, tenantId, pollClaimedAt, CancellationToken.None);
            if (notFound == "deviceauth_not_found")
                return new ChatGptDevicePollResult(false, "error", state,
                    "Device session not found at OpenAI — it may have expired or was never authorized. Restart linking to get a new code.");
            return new ChatGptDevicePollResult(false, "error", state, "Device poll failed.");
        }

        // While the human hasn't authorized yet, OpenAI returns 403 with
        // {"error": {"code": "deviceauth_authorization_pending"}}. Keep polling.
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var pendingCode = TryGetNestedErrorCode(raw);
            await _pending.TryReleaseClaimAsync(flow.Id, tenantId, pollClaimedAt, CancellationToken.None);
            if (pendingCode == "deviceauth_authorization_pending" || pendingCode == "deviceauth_slow_down")
                return new ChatGptDevicePollResult(false, "pending", state, null);
            return new ChatGptDevicePollResult(false, "error", state, "Device poll rejected.");
        }
        if (!resp.IsSuccessStatusCode)
        {
            await _pending.TryReleaseClaimAsync(flow.Id, tenantId, pollClaimedAt, CancellationToken.None);
            return new ChatGptDevicePollResult(false, "error", state, "Device poll failed.");
        }
        var doc = JsonSerializer.Deserialize<JsonElement>(raw, _json);
        var authCode = doc.GetString("authorization_code");
        var verifier = doc.GetString("code_verifier");
        if (string.IsNullOrEmpty(authCode) || string.IsNullOrEmpty(verifier))
        {
            await _pending.TryReleaseClaimAsync(flow.Id, tenantId, pollClaimedAt, CancellationToken.None);
            return new ChatGptDevicePollResult(false, "pending", state, null);
        }

        // Keep provider-issued credentials server-side. The browser receives only
        // the opaque state and readiness flag; completion never trusts a verifier
        // supplied by the caller. The claim is retained until this state is durable.
        flow.SetDeviceCredentials(authCode, verifier, _vault, allowClaimedFlow: true);
        var persisted = false;
        Exception? persistenceFailure = null;
        for (var attempt = 0; attempt < 2 && !persisted; attempt++)
        {
            try
            {
                persisted = await _pending.PersistDeviceCredentialsAsync(
                    flow, tenantId, pollClaimedAt, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                persistenceFailure = ex;
            }
        }
        if (!persisted)
            throw new InvalidOperationException(
                "Device authorization credentials could not be persisted for recovery.", persistenceFailure);
        authorizationReceived = true;
        try
        {
            await _pending.TryReleaseClaimAsync(flow.Id, tenantId, pollClaimedAt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Device poll claim release deferred for flow {FlowId}.", flow.Id);
        }
        return new ChatGptDevicePollResult(true, "authorized", state, null);
    }
    catch
    {
        if (!authorizationReceived)
            await _pending.TryReleaseClaimAsync(flow.Id, tenantId, pollClaimedAt, CancellationToken.None);
        throw;
    }
}

    private static string? TryGetNestedErrorCode(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var root = d.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                if (err.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                    return code.GetString();
            }
            // Fallback: some responses put code at the root.
            if (root.TryGetProperty("code", out var rc) && rc.ValueKind == JsonValueKind.String)
                return rc.GetString();
        }
        catch { /* ignore */ }
        return null;
    }

/// <summary>Exchanges the server-held device credentials for tokens and stores them.</summary>
public async Task<OAuthCallbackResult> CompleteChatGptDeviceAsync(string state, CancellationToken ct = default)
{
    var tenantId = CurrentTenant();
    var flow = await _pending.GetByStateAsync(state, tenantId, ct);
    if (flow is null || flow.IsExpired)
        return new OAuthCallbackResult(OAuthTokenStatus.Error, "Unknown or expired device flow.");
    if (flow.HasCompletionFailure)
        return new OAuthCallbackResult(OAuthTokenStatus.Error, "OAuth completion failed; restart the connection.");

    var targetCode = flow.AiProviderCode ?? "chatgpt";
    var provider = await ResolveProviderAsync(targetCode, ct, tenantId);
    if (provider is null)
        return new OAuthCallbackResult(OAuthTokenStatus.Error, $"No AiProvider '{targetCode}'.");
    var cfg = await _configs.GetByCodeAsync("chatgpt", ct)
        ?? throw new InvalidOperationException("No OAuth config for 'chatgpt'.");

    if (flow.HasCompletionTokens)
    {
        var linkedToken = await FindCompletionTokenAsync(flow, provider, ct);
        if (linkedToken is not null)
            return await FinalizePersistedCompletionAsync(flow, provider, cfg, linkedToken, true, ct);
        return await FinalizePendingCompletionAsync(flow, provider, cfg, true, ct);
    }

    var storedAuthorizationCode = flow.DecryptAuthorizationCode(_vault);
    var storedCodeVerifier = flow.DecryptCodeVerifier(_vault);
    if (string.IsNullOrEmpty(storedAuthorizationCode) || string.IsNullOrEmpty(storedCodeVerifier))
        return new OAuthCallbackResult(OAuthTokenStatus.Error, "Device flow is not ready for completion.");

    var completionClaimedAt = DateTimeOffset.UtcNow;
    if (!await _pending.TryClaimCompletionAsync(flow.Id, tenantId, completionClaimedAt, ct))
    {
        var currentFlow = await _pending.GetByStateAsync(state, tenantId, ct) ?? flow;
        var linkedToken = await FindCompletionTokenAsync(currentFlow, provider, ct);
        if (linkedToken is not null)
            return await FinalizePersistedCompletionAsync(currentFlow, provider, cfg, linkedToken, true, ct);
        if (currentFlow.HasCompletionTokens)
            return await FinalizePendingCompletionAsync(currentFlow, provider, cfg, true, ct);
        return new OAuthCallbackResult(OAuthTokenStatus.Error, "Device flow completion is already in progress.");
    }

    var exchangeCompleted = false;
    var completionTokenDurable = false;
    try
    {
        var client = _http.CreateClient("oauth");
        using var exchange = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = cfg.ClientId,
            ["code"] = storedAuthorizationCode,
            ["code_verifier"] = storedCodeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = "https://auth.openai.com/deviceauth/callback",
        });
        using var resp = await client.PostAsync(cfg.TokenEndpoint, exchange, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return await ReleaseCompletionAndReturnAsync(flow, tenantId, completionClaimedAt, "Token exchange failed.");

        exchangeCompleted = true;
        var doc = JsonSerializer.Deserialize<JsonElement>(raw, _json);
        var access = doc.GetString("access_token");
        if (string.IsNullOrEmpty(access))
            return await MarkCompletionFailureAsync(
                flow,
                tenantId,
                completionClaimedAt,
                deviceCompletionClaim: true,
                "OAuth provider returned an incomplete token response.");

        var refresh = doc.GetString("refresh_token");
        var expiresIn = doc.GetInt32OrDefault("expires_in");
        var expiresAt = expiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresIn) : (DateTimeOffset?)null;
        flow.StoreCompletionTokens(
            access,
            refresh,
            doc.GetString("token_type") ?? "Bearer",
            expiresAt,
            null,
            _vault,
            allowUnclaimedDeviceFlow: true);
        await _pending.UpdateAsync(flow, ct);
        var completionToken = await PersistCompletionTokenAsync(flow, provider, ct);
        completionTokenDurable = true;
        return await FinalizePersistedCompletionAsync(flow, provider, cfg, completionToken, true, ct);
    }
    catch (OperationCanceledException)
    {
        if (exchangeCompleted)
        {
            if (!flow.HasCompletionTokens)
                await MarkCompletionFailureAsync(
                    flow,
                    flow.TenantId,
                    completionClaimedAt,
                    deviceCompletionClaim: true,
                    "OAuth provider returned an unusable token response.");
            else
                await TryPersistCompletionTokenForRecoveryAsync(flow, provider, completionTokenDurable);
        }
        else
            await _pending.TryReleaseCompletionClaimAsync(flow.Id, flow.TenantId, completionClaimedAt, CancellationToken.None);
        throw;
    }
    catch (Exception ex)
    {
        if (exchangeCompleted)
        {
            if (!flow.HasCompletionTokens)
                return await MarkCompletionFailureAsync(
                    flow,
                    flow.TenantId,
                    completionClaimedAt,
                    deviceCompletionClaim: true,
                    "OAuth provider returned an unusable token response.");
            await TryPersistCompletionTokenForRecoveryAsync(flow, provider, completionTokenDurable);
        }
        else
            await _pending.TryReleaseCompletionClaimAsync(flow.Id, flow.TenantId, completionClaimedAt, CancellationToken.None);
        _log.LogWarning("OAuth callback exchange failed with {ExceptionType}.", ex.GetType().Name);
        return new OAuthCallbackResult(
            OAuthTokenStatus.Error,
            exchangeCompleted
                ? "OAuth exchange completed; local finalization is pending. Retry the callback or recovery endpoint."
                : "OAuth callback exchange failed.");
    }
}



    private async Task<OAuthCallbackResult> MarkCompletionFailureAsync(
        OAuthPendingFlow flow,
        Guid tenantId,
        DateTimeOffset claimAt,
        bool deviceCompletionClaim,
        string failure)
    {
        try
        {
            flow.MarkCompletionFailure(failure, DateTimeOffset.UtcNow);
            var persisted = await _pending.PersistCompletionFailureAsync(
                flow, tenantId, claimAt, deviceCompletionClaim, CancellationToken.None);
            return new OAuthCallbackResult(OAuthTokenStatus.Error,
                persisted
                    ? "OAuth completion failed; restart the connection."
                    : "OAuth completion failed; recovery is required.");
        }
        catch (Exception ex)
        {
            _log.LogWarning("OAuth completion failure state could not be persisted ({ExceptionType}).", ex.GetType().Name);
            return new OAuthCallbackResult(OAuthTokenStatus.Error, "OAuth completion failed; recovery is required.");
        }
    }

    private async Task TryPersistCompletionTokenForRecoveryAsync(
        OAuthPendingFlow flow,
        AiProvider? provider,
        bool alreadyDurable)
    {
        if (alreadyDurable || provider is null || !flow.HasCompletionTokens)
            return;

        try
        {
            await PersistCompletionTokenAsync(flow, provider, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning("OAuth completion durability retry failed with {ExceptionType}.", ex.GetType().Name);
        }
    }

    private async Task<OAuthCallbackResult> ReleaseCompletionAndReturnAsync(
        OAuthPendingFlow flow, Guid tenantId, DateTimeOffset claimedAt, string message)
    {
        await _pending.TryReleaseCompletionClaimAsync(flow.Id, tenantId, claimedAt, CancellationToken.None);
        return new OAuthCallbackResult(OAuthTokenStatus.Error, message);
    }

    private async Task<OAuthCallbackResult> ReleaseAndReturnAsync(
        OAuthPendingFlow flow, Guid tenantId, DateTimeOffset claimedAt, string message)
    {
        await _pending.TryReleaseClaimAsync(flow.Id, tenantId, claimedAt, CancellationToken.None);
        return new OAuthCallbackResult(OAuthTokenStatus.Error, message);
    }

    /// <summary>
    /// Finds the active pending device flow for an account code and returns
    /// its current polling status. Used by the admin poll endpoint.
    /// </summary>
    public async Task<ChatGptDeviceStatus?> GetChatGptDeviceStatusAsync(string accountCode, CancellationToken ct = default)
    {
    var flow = await _pending.GetActiveByProviderCodeAsync(accountCode, CurrentTenant(), ct);
    if (flow is null)
        return null;
    if (flow.IsExpired)
        return new ChatGptDeviceStatus("error", false, flow.State, "Device flow expired. Restart linking.");

    var poll = await PollChatGptDeviceAsync(flow.State, ct);
    return new ChatGptDeviceStatus(
        poll.Status, poll.Success, poll.State, poll.Error);
    }

    /// <summary>Extracts chatgpt_account_id from the JWT (id_token/access_token claim).</summary>
    private static string? ExtractChatGptAccountId(string jwt)
    {
    try
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        var payload = parts[1].TrimEnd('=');
        // base64url decode
        var pad = payload.Length % 4 == 0 ? "" : new string('=', 4 - (payload.Length % 4));
        var json = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/') + pad));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("chatgpt_account_id", out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        if (doc.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth) &&
            auth.ValueKind == JsonValueKind.Object &&
            auth.TryGetProperty("chatgpt_account_id", out var aid) && aid.ValueKind == JsonValueKind.String)
            return aid.GetString();
    }
    catch { /* best-effort label */ }
    return null;
}

    private static bool IsSafeOAuthState(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= 256
            && value.All(ch => ch is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-' or '_' or '.' or '~');

    private static bool IsSafeOAuthValue(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(ch => ch is >= '\x21' and <= '\x7e');
}
internal static class JsonElementExtensions
{
    public static string? GetString(this JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static int GetInt32OrDefault(this JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    public static int GetInt32OrDefault(this JsonElement e)
        => e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 0;
}
