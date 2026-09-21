using Arkana.Domain.Services;

namespace Arkana.Domain.Entities;

/// <summary>
/// In-flight OAuth authorization-code exchange state, persisted so a server
/// restart mid-flow does not lose the PKCE verifier/state. Keyed by the CSRF
/// <see cref="State"/>. Short-lived: cleared on completion or expiry.
/// </summary>
public sealed class OAuthPendingFlow
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Navigation to the owning tenant (ENT-ARKANA-001 scoping).</summary>
    public Tenant Tenant { get; private set; } = null!;

    /// <summary>Provider template code (gemini / openai / grok).</summary>
    public string ProviderCode { get; private set; } = string.Empty;

    /// <summary>
    /// Target AiProvider code for multi-account scenarios (e.g. "gemini-acc1").
    /// Null when binding to the provider matching the config code (original single-account flow).
    /// </summary>
    public string? AiProviderCode { get; private set; }

    /// <summary>CSRF token echoed back by the provider; also the lookup key.</summary>
    public string State { get; private set; } = string.Empty;

    /// <summary>PKCE code_verifier (sealed — it is secret-equivalent).</summary>
    public string? SealedCodeVerifier { get; private set; }

    /// <summary>
    /// Device-flow authorization code returned by the provider (sealed). It is
    /// kept server-side so completion cannot be redirected to another device
    /// session by a caller-supplied code.
    /// </summary>
    public string? SealedAuthorizationCode { get; private set; }

    /// <summary>The exact redirect_uri sent to the provider (must match).</summary>
    public string RedirectUri { get; private set; } = string.Empty;

    /// <summary>
    /// Device-authorization id (ChatGPT/Codex device-code grant). Null for
    /// authorization-code flows. Persisted so the gateway can poll the token
    /// endpoint after the user enters the user_code on another device.
    /// </summary>
    public string? DeviceAuthId { get; private set; }

    /// <summary>
    /// The user_code shown to the admin for a ChatGPT device flow. Persisted so
    /// the gateway can include it when polling the device token endpoint
    /// (OpenAI requires both device_auth_id and user_code on the poll call).
    /// </summary>
    public string? DeviceUserCode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    /// <summary>Sealed access token received from a completed exchange awaiting local finalization.</summary>
    public string? SealedCompletionAccessToken { get; private set; }

    /// <summary>Sealed refresh token received from a completed exchange awaiting local finalization.</summary>
    public string? SealedCompletionRefreshToken { get; private set; }

    public string? CompletionTokenType { get; private set; }
    public DateTimeOffset? CompletionExpiresAt { get; private set; }
    public DateTimeOffset? CompletionRefreshExpiresAt { get; private set; }

    /// <summary>True when the external token exchange completed and can be finalized without replaying the code.</summary>
    public bool HasCompletionTokens => !string.IsNullOrWhiteSpace(SealedCompletionAccessToken);

    /// <summary>True when completion reached a terminal provider-response failure.</summary>
    public bool HasCompletionFailure => !string.IsNullOrWhiteSpace(CompletionFailure);

    public string? CompletionFailure { get; private set; }
    public DateTimeOffset? CompletionFailedAt { get; private set; }

    public void MarkCompletionFailure(string failure, DateTimeOffset failedAt)
    {
        if (string.IsNullOrWhiteSpace(failure))
            throw new ArgumentException("Completion failure is required.", nameof(failure));
        CompletionFailure = failure.Length <= 256 ? failure : failure[..256];
        CompletionFailedAt = failedAt;
    }

    /// <summary>
    /// Stores the provider response sealed while the local token/provider/pending-flow
    /// finalization is retried. The plaintext response never enters persistence.
    /// </summary>
    public void StoreCompletionTokens(
        string accessToken,
        string? refreshToken,
        string tokenType,
        DateTimeOffset? expiresAt,
        DateTimeOffset? refreshExpiresAt,
        ICredentialVault vault,
        bool allowUnclaimedDeviceFlow = false)
    {
        if (ClaimedAt is null && !allowUnclaimedDeviceFlow)
            throw new InvalidOperationException("The OAuth flow must be claimed before storing completion state.");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required.", nameof(accessToken));

        SealedCompletionAccessToken = vault.Seal(accessToken);
        SealedCompletionRefreshToken = refreshToken is null ? null : vault.Seal(refreshToken);
        CompletionTokenType = tokenType;
        CompletionExpiresAt = expiresAt;
        CompletionRefreshExpiresAt = refreshExpiresAt;
    }

    public string? DecryptCompletionAccessToken(ICredentialVault vault)
        => string.IsNullOrEmpty(SealedCompletionAccessToken) ? null : vault.Open(SealedCompletionAccessToken);

    public string? DecryptCompletionRefreshToken(ICredentialVault vault)
        => string.IsNullOrEmpty(SealedCompletionRefreshToken) ? null : vault.Open(SealedCompletionRefreshToken);



    public DateTimeOffset? ClaimedAt { get; private set; }
    public DateTimeOffset? CompletionClaimedAt { get; private set; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    public bool TryClaim(DateTimeOffset claimedAt)
    {
        if (ClaimedAt is not null) return false;
        ClaimedAt = claimedAt;
        return true;
    }

    /// <summary>Stores provider-issued device credentials before completion.</summary>
    public void SetDeviceCredentials(
        string authorizationCode,
        string codeVerifier,
        ICredentialVault vault,
        bool allowClaimedFlow = false)
    {
        if (ClaimedAt is not null && !allowClaimedFlow)
            throw new InvalidOperationException("The device flow has already been claimed.");
        if (string.IsNullOrWhiteSpace(authorizationCode))
            throw new ArgumentException("Authorization code is required.", nameof(authorizationCode));
        if (string.IsNullOrWhiteSpace(codeVerifier))
            throw new ArgumentException("Code verifier is required.", nameof(codeVerifier));

        SealedAuthorizationCode = vault.Seal(authorizationCode);
        SealedCodeVerifier = vault.Seal(codeVerifier);
    }

    private OAuthPendingFlow() { }

    public static OAuthPendingFlow Create(
        Guid tenantId, string providerCode, string state, string codeVerifierPlaintext,
        string redirectUri, ICredentialVault vault, TimeSpan ttl,
        string? aiProviderCode = null, string? deviceAuthId = null, string? deviceUserCode = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new OAuthPendingFlow
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProviderCode = providerCode.ToLowerInvariant(),
            AiProviderCode = aiProviderCode,
            State = state,
            SealedCodeVerifier = vault.Seal(codeVerifierPlaintext),
            RedirectUri = redirectUri,
            DeviceAuthId = deviceAuthId,
            DeviceUserCode = deviceUserCode,
            CreatedAt = now,
            ExpiresAt = now.Add(ttl),
        };
    }

    public string? DecryptCodeVerifier(ICredentialVault vault)
        => string.IsNullOrEmpty(SealedCodeVerifier) ? null : vault.Open(SealedCodeVerifier);

    public string? DecryptAuthorizationCode(ICredentialVault vault)
        => string.IsNullOrEmpty(SealedAuthorizationCode) ? null : vault.Open(SealedAuthorizationCode);
}
