namespace Arkana.Domain.Interfaces;

using Arkana.Domain.Entities;

/// <summary>
/// Drives server-orchestrated OAuth: the gateway builds the
/// provider authorize URL (PKCE + state), the browser redirects to the provider,
/// the provider redirects back to the gateway's callback, and the gateway
/// exchanges the code for tokens server-side. Employees never touch secrets —
/// the client id/secret are platform-held (seeded from env). Tokens are stored
/// sealed and auto-refreshed; the connector resolves them via
/// <see cref="IOAuthTokenResolver"/>.
/// </summary>
public interface IOAuthFlowService
{
    /// <summary>
    /// Begins an authorization-code + PKCE flow for a provider template
    /// (by <paramref name="providerCode"/>, e.g. "gemini"). Returns the real
    /// provider authorize URL (with redirect_uri = {redirectBase}/oauth/{code}/callback).
    /// Persists the PKCE verifier + state so a restart mid-flow is safe.
    /// </summary>
    Task<OAuthStartResult> StartAsync(string providerCode, string redirectBase, CancellationToken ct = default);

    /// <summary>
    /// Handles the provider's redirect back to the gateway callback: validates
    /// state (CSRF), exchanges code→token server-side, stores the sealed token
    /// against the matching <see cref="AiProvider"/> (by Code), and flips that
    /// provider to OAuth auth. Returns the resulting status for the HTML echo.
    /// </summary>
    Task<OAuthCallbackResult> HandleCallbackAsync(string providerCode, string code, string state, CancellationToken ct = default);

    /// <summary>
    /// Finalizes a previously exchanged callback from its sealed, durable pending
    /// state without re-exchanging the one-time authorization code.
    /// </summary>
    Task<OAuthCallbackResult> RecoverCallbackAsync(string providerCode, string state, CancellationToken ct = default);

    /// <summary>Connection status for a provider (by code) for the UI / connector.</summary>
    Task<OAuthConnectionStatus> GetStatusAsync(string providerCode, CancellationToken ct = default);

    /// <summary>Revokes (clears + flips provider back to ApiKey) for a provider (by code).</summary>
    Task DisconnectAsync(string providerCode, CancellationToken ct = default);

    /// <summary>
    /// Returns a usable (auto-refreshed) access token for a provider, or null
    /// when the provider is not OAuth or has no valid token. Refreshes in-place
    /// when the token is within <paramref name="refreshSlack"/> of expiry.
    /// Used by the connector layer (keyed by AiProvider id).
    /// </summary>
    Task<string?> GetValidAccessTokenAsync(Guid providerId, TimeSpan? refreshSlack = null, CancellationToken ct = default);
    Task<string?> GetValidAccessTokenForTenantAsync(Guid providerId, Guid tenantId, TimeSpan? refreshSlack = null, CancellationToken ct = default);

    // ── ChatGPT / Codex device-code flow (server-side; no localhost browser) ──

    /// <summary>Begins a Codex device-code flow for a ChatGPT account provider (by code).</summary>
    Task<ChatGptDeviceStartResult> StartChatGptDeviceAsync(string aiProviderCode, CancellationToken ct = default);

    /// <summary>Polls the device token endpoint; yields authorization_code + verifier when authorized.</summary>
    Task<ChatGptDevicePollResult> PollChatGptDeviceAsync(string state, CancellationToken ct = default);

    /// <summary>Exchanges server-held device credentials for tokens and stores them sealed.</summary>
    Task<OAuthCallbackResult> CompleteChatGptDeviceAsync(string state, CancellationToken ct = default);

    /// <summary>
    /// Convenience for the poll endpoint: finds the active pending device flow
    /// for an account code (chatgpt-accN) and returns its current polling
    /// status (pending / authorized+code). Returns null when no flow is active.
    /// </summary>
    Task<ChatGptDeviceStatus?> GetChatGptDeviceStatusAsync(string accountCode, CancellationToken ct = default);
}

public sealed record ChatGptDeviceStartResult(
    bool Success, string? UserCode, string? VerificationUrl, string? Error, string? State = null);

public sealed record ChatGptDevicePollResult(
    bool Success, string Status, string? State, string? Error);

/// <summary>Poll status returned to the admin UI while linking a ChatGPT account.</summary>
public sealed record ChatGptDeviceStatus(
    string Status, // "pending" | "authorized" | "error" | "unknown"
    bool Ready,    // true when AuthorizationCode is present
    string? State = null,
    string? Error = null);

public sealed record OAuthStartResult(
    string ProviderCode,
    string AuthorizationUrl,
    string State,
    string DisplayName,
    bool IsDeviceCode = false,
    string? UserCode = null,
    string? VerificationUrl = null);

public sealed record OAuthCallbackResult(
    OAuthTokenStatus Status,
    string? ErrorMessage = null);

public sealed record OAuthConnectionStatus(
    string ProviderCode,
    OAuthTokenStatus Status,
    DateTimeOffset? ExpiresAt,
    bool HasRefreshToken,
    string? LastError);
