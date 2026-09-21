namespace Arkana.Domain.Entities;

/// <summary>
/// OAuth grant type the gateway uses to obtain tokens for a provider.
/// </summary>
public enum OAuthGrant
{
    /// <summary>Device authorization flow (user types a code on another device).</summary>
    DeviceCode,

    /// <summary>Authorization-code flow with PKCE (user redirects back to the gateway).</summary>
    AuthorizationCode
}
