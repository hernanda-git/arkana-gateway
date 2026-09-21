using Arkana.Domain.Services;

namespace Arkana.Domain.Entities;

/// <summary>
/// Per-provider OAuth connection state. Tenant-scoped: each <see cref="AiProvider"/>
/// configured for OAuth owns exactly one (typically) connection. Access and
/// refresh tokens are stored sealed via <see cref="ICredentialVault"/>.
/// </summary>
public sealed class ProviderOAuthToken
{
    public Guid Id { get; private set; }
    public Guid AiProviderId { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Display label for multi-account identification (e.g. "Budi's Google"). Null for single-account tokens.</summary>
    public string? Label { get; private set; }

    public string? SealedAccessToken { get; private set; }
    public string? SealedRefreshToken { get; private set; }
    public string TokenType { get; private set; } = "Bearer";
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? RefreshExpiresAt { get; private set; }

    public OAuthTokenStatus Status { get; private set; } = OAuthTokenStatus.Pending;
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid Version { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Links a durably stored token response to the claimed OAuth flow until local
    /// provider/pending-flow finalization completes. This prevents a retry from
    /// re-exchanging a one-time authorization code.
    /// </summary>
    public Guid? CompletionFlowId { get; private set; }

    // Navigation
    public AiProvider? Provider { get; private set; }
    public Tenant Tenant { get; private set; } = null!;

    private ProviderOAuthToken() { }

    public static ProviderOAuthToken CreatePending(Guid aiProviderId, Guid tenantId, string? label = null)
        => new()
        {
            Id = Guid.NewGuid(),
            AiProviderId = aiProviderId,
            TenantId = tenantId,
            Label = label,
            Status = OAuthTokenStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    /// <summary>
    /// Persists exchanged tokens (sealed). Marks the connection <see cref="OAuthTokenStatus.Connected"/>.
    /// </summary>
    public void StoreTokens(string accessTokenSealed, string? refreshTokenSealed,
        string tokenType, DateTimeOffset? expiresAt, DateTimeOffset? refreshExpiresAt)
    {
        SealedAccessToken = accessTokenSealed;
        SealedRefreshToken = refreshTokenSealed;
        TokenType = tokenType;
        ExpiresAt = expiresAt;
        RefreshExpiresAt = refreshExpiresAt;
        Status = OAuthTokenStatus.Connected;
        LastError = null;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version = Guid.NewGuid();
    }

    public void BindCompletionFlow(Guid flowId)
    {
        if (flowId == Guid.Empty)
            throw new ArgumentException("Completion flow ID is required.", nameof(flowId));
        CompletionFlowId = flowId;
    }


    /// <summary>Rotates the access token during a refresh, preserving the refresh token.</summary>
    public void RefreshTokens(string accessTokenSealed, string? refreshTokenSealed,
        string tokenType, DateTimeOffset? expiresAt, DateTimeOffset? refreshExpiresAt)
    {
        StoreTokens(accessTokenSealed, refreshTokenSealed, tokenType, expiresAt, refreshExpiresAt);
    }

    public void MarkExpired()
    {
        Status = OAuthTokenStatus.Expired;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version = Guid.NewGuid();
    }

    public void MarkError(string message)
    {
        LastError = message;
        Status = OAuthTokenStatus.Error;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version = Guid.NewGuid();
    }

    /// <summary>Records a retryable refresh failure without disconnecting a still-usable token.</summary>
    public void MarkTransientError(string message)
    {
        LastError = message;
        Status = OAuthTokenStatus.Connected;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version = Guid.NewGuid();
    }

    public void Revoke()
    {
        SealedAccessToken = null;
        SealedRefreshToken = null;
        ExpiresAt = null;
        RefreshExpiresAt = null;
        Status = OAuthTokenStatus.Revoked;
        LastError = null;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version = Guid.NewGuid();
    }

    /// <summary>Decrypts the access token for an outbound request. Null when none.</summary>
    public string? DecryptAccessToken(ICredentialVault vault)
        => string.IsNullOrEmpty(SealedAccessToken) ? null : vault.Open(SealedAccessToken);

    /// <summary>Decrypts the refresh token for a refresh request. Null when none.</summary>
    public string? DecryptRefreshToken(ICredentialVault vault)
        => string.IsNullOrEmpty(SealedRefreshToken) ? null : vault.Open(SealedRefreshToken);

    /// <summary>True when the access token is within <paramref name="slack"/> of expiry.</summary>
    public bool NeedsRefresh(TimeSpan slack)
        => Status == OAuthTokenStatus.Connected
           && ExpiresAt is { } exp
           && exp - DateTimeOffset.UtcNow <= slack;
}
