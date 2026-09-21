using Arkana.Domain.Services;

namespace Arkana.Domain.Entities;

/// <summary>
/// A reusable OAuth client template for a platform (OpenRouter, Anthropic, …).
/// Many <see cref="AiProvider"/> rows can reference one config. The client
/// secret (when present) is stored sealed via <see cref="ICredentialVault"/>.
/// </summary>
public sealed class OAuthProviderConfig
{
    public Guid Id { get; private set; }
    public string ProviderCode { get; private set; } = string.Empty;   // e.g. "openrouter"
    public string DisplayName { get; private set; } = string.Empty;    // "OpenRouter (OAuth)"
    public OAuthGrant GrantType { get; private set; } = OAuthGrant.DeviceCode;
    public string? AuthorizationEndpoint { get; private set; }
    public string TokenEndpoint { get; private set; } = string.Empty;
    public string? DeviceAuthorizationEndpoint { get; private set; }
    public string ClientId { get; private set; } = string.Empty;
    public string? SealedClientSecret { get; private set; }             // sealed; null for public clients
    public string? Scopes { get; private set; }                        // space-separated
    public string? ExtraAuthParams { get; private set; }             // JSON, provider-specific

    /// <summary>
    /// ChatGPT/Codex-specific inference + device endpoints, when present.
    /// Carried in <see cref="ExtraAuthParams"/> (JSON) so no schema migration
    /// is required. Keys: <c>inferenceBaseUrl</c>, <c>deviceUsercodeUrl</c>,
    /// <c>deviceTokenUrl</c>.
    /// </summary>
    public ChatGptEndpoints? ChatGptEndpoints
    {
        get
        {
            if (string.IsNullOrEmpty(ExtraAuthParams)) return null;
            try { return System.Text.Json.JsonSerializer.Deserialize<ChatGptEndpoints>(ExtraAuthParams); }
            catch { return null; }
        }
    }

    private OAuthProviderConfig() { }

    public static OAuthProviderConfig Create(
        string providerCode, string displayName, OAuthGrant grantType,
        string tokenEndpoint, string clientId,
        string? authorizationEndpoint = null, string? deviceAuthorizationEndpoint = null,
        string? clientSecretPlaintext = null, ICredentialVault? vault = null,
        string? scopes = null, string? extraAuthParams = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerCode);
        ArgumentException.ThrowIfNullOrEmpty(tokenEndpoint);
        // ClientId may be empty for public/client-secret-less templates (e.g. a
        // device-code provider configured later); the actual flow validates it.
        clientId = clientId ?? string.Empty;

        return new OAuthProviderConfig
        {
            Id = Guid.NewGuid(),
            ProviderCode = providerCode.ToLowerInvariant(),
            DisplayName = displayName,
            GrantType = grantType,
            TokenEndpoint = tokenEndpoint,
            ClientId = clientId,
            AuthorizationEndpoint = authorizationEndpoint,
            DeviceAuthorizationEndpoint = deviceAuthorizationEndpoint,
            // SECURITY: seal the secret at creation time; never store plaintext.
            SealedClientSecret = (clientSecretPlaintext is null || vault is null)
                ? null
                : vault.Seal(clientSecretPlaintext),
            Scopes = scopes,
            ExtraAuthParams = extraAuthParams
        };
    }

    /// <summary>Decrypts the client secret for use in a token request. Null when none.</summary>
    public string? DecryptClientSecret(ICredentialVault vault)
        => string.IsNullOrEmpty(SealedClientSecret) ? null : vault.Open(SealedClientSecret);

    /// <summary>
    /// Updates only the requested scope contract. Used by controlled, idempotent
    /// template reconciliation; client credentials and endpoints are preserved.
    /// </summary>
    public void UpdateScopes(string? scopes) => Scopes = scopes;

    public void UpdateEndpoints(
        string? authorizationEndpoint, string tokenEndpoint, string? deviceAuthorizationEndpoint,
        string clientId, string? scopes, string? extraAuthParams,
        string? clientSecretPlaintext = null, ICredentialVault? vault = null)
    {
        AuthorizationEndpoint = authorizationEndpoint;
        TokenEndpoint = tokenEndpoint;
        DeviceAuthorizationEndpoint = deviceAuthorizationEndpoint;
        ClientId = clientId;
        Scopes = scopes;
        ExtraAuthParams = extraAuthParams;
        if (clientSecretPlaintext is not null && vault is not null)
            SealedClientSecret = vault.Seal(clientSecretPlaintext);
    }
}

/// <summary>
/// Summary shape returned to the UI for the OAuth-config picker.
/// </summary>
public sealed record OAuthProviderConfigSummary(
    Guid Id, string ProviderCode, string DisplayName, OAuthGrant GrantType);

/// <summary>
/// ChatGPT/Codex-specific endpoints, serialized into
/// <see cref="OAuthProviderConfig.ExtraAuthParams"/> JSON. These are OpenAI's
/// first-party Codex OAuth surfaces (the same ones OpenCode and Hermes use).
/// </summary>
public sealed record ChatGptEndpoints(
    string InferenceBaseUrl = "https://chatgpt.com/backend-api/codex",
    string DeviceUsercodeUrl = "https://auth.openai.com/api/accounts/deviceauth/usercode",
    string DeviceTokenUrl = "https://auth.openai.com/api/accounts/deviceauth/token")
{
    public static ChatGptEndpoints Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new ChatGptEndpoints();
        try { return System.Text.Json.JsonSerializer.Deserialize<ChatGptEndpoints>(json) ?? new ChatGptEndpoints(); }
        catch { return new ChatGptEndpoints(); }
    }
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);
}
