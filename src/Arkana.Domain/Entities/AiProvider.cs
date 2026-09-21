using Arkana.Domain.Services;

namespace Arkana.Domain.Entities;

/// <summary>
/// Represents an AI provider configuration (OpenCode, OpenAI, Gemini, etc.).
///
/// SECURITY: The <see cref="ApiKey"/> property stores the **sealed** (encrypted)
/// form of the provider credential — never plaintext. Use
/// <see cref="SealApiKey"/> when writing and <see cref="DecryptApiKey"/>
/// when reading. The provider services call <see cref="DecryptApiKey"/>
/// just-in-time before making the upstream HTTP request.
///
/// URL SAFETY (SEC-ARKANA-004): The <see cref="BaseUrl"/> is validated against
/// SSRF defenses (scheme/host/IP blocklist) at construction time. Literal-only
/// check here; DNS-based validation happens at HTTP-call time in the
/// infrastructure layer.
/// </summary>
public sealed class AiProvider
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;         // e.g., "opencode", "openai"
    public string? BaseUrl { get; private set; }

    /// <summary>
    /// Sealed (envelope-encrypted) form of the provider API key.
    /// Empty or null when no key is configured. Never holds plaintext.
    /// </summary>
    public string? ApiKey { get; private set; }

    /// <summary>
    /// Authentication method used to talk to the upstream. <see cref="AuthMethod.ApiKey"/>
    /// (default) uses the sealed <see cref="ApiKey"/>; <see cref="AuthMethod.OAuth"/> brokers
    /// an interactive OAuth token stored sealed in <c>ProviderOAuthTokens</c>.
    /// </summary>
    public AuthMethod AuthMethod { get; private set; } = AuthMethod.ApiKey;

    /// <summary>
    /// References the <see cref="OAuthProviderConfig"/> template when
    /// <see cref="AuthMethod"/> is <see cref="AuthMethod.OAuth"/>. Null otherwise.
    /// </summary>
    public Guid? OAuthConfigId { get; private set; }

    public bool UsesOAuth => AuthMethod == AuthMethod.OAuth;

    /// <summary>
    /// The ChatGPT account id (the <c>chatgpt_account_id</c> JWT claim) that the
    /// Codex API requires as the <c>ChatGPT-Account-Id</c> header. Populated when
    /// the device OAuth flow completes. Null until the account has been linked.
    /// </summary>
    public string? AccountId { get; private set; }

    public int Priority { get; private set; }                         // Internal failover ordering
    public bool IsEnabled { get; private set; } = true;
    public int? MaxTokensPerRequest { get; private set; }
    public decimal CostPerInputToken { get; private set; }
    public decimal CostPerOutputToken { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    // Navigation
    public ICollection<Model> Models { get; private set; } = [];
    public OAuthProviderConfig? OAuthConfig { get; private set; }

    // Multi-tenant scoping (ENT-ARKANA-001)
    public Guid TenantId { get; private set; }
    public Tenant Tenant { get; private set; } = null!;

    private AiProvider() { }

    /// <summary>
    /// Factory that validates the BaseUrl against SSRF defenses before
    /// constructing the entity. The validator is a synchronous, literal-only
    /// check (no DNS resolution) — DNS-based validation happens at HTTP-call
    /// time in the infrastructure layer.
    /// </summary>
    /// <param name="validator">
    /// Optional literal URL validator. Pass null only in tests that don't
    /// care about SSRF (the entity is constructed with the URL unvalidated).
    /// </param>
    /// <param name="vault">
    /// Credential vault that seals the plaintext API key before storage.
    /// Pass null when no credential is being set (BaseUrl-only updates,
    /// or tests that don't exercise the credential path).
    /// </param>
    public static AiProvider Create(string name, string code, int priority,
        string? baseUrl = null, string? apiKeyPlaintext = null,
        ICredentialVault? vault = null, UrlSafetyValidator? validator = null,
        decimal costPerInput = 0, decimal costPerOutput = 0)
    {
        // SSRF defense: reject unsafe URLs at construction time so the
        // entity itself can never hold a non-routable target.
        if (baseUrl is not null && validator is not null)
            baseUrl = validator.ValidateLiteral(baseUrl).ToString().TrimEnd('/');

        return new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = name,
            Code = code.ToLowerInvariant(),
            Priority = priority,
            BaseUrl = baseUrl,
            // SECURITY: encrypt the API key before it touches the entity property.
            // If no vault is provided, the value is left null (caller's responsibility).
            ApiKey = (apiKeyPlaintext is null || vault is null) ? null : vault.Seal(apiKeyPlaintext),
            IsEnabled = true,
            CostPerInputToken = costPerInput,
            CostPerOutputToken = costPerOutput,
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001")
        };
    }

    public void AssignTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        TenantId = tenantId;
    }

    public void Enable() => IsEnabled = true;
    public void Disable() => IsEnabled = false;

    /// <summary>
    /// Records the ChatGPT account id surfaced by the device OAuth token. The
    /// Codex API needs it as the <c>ChatGPT-Account-Id</c> header, so it must
    /// be persisted at link time (not just logged).
    /// </summary>
    public void SetAccountId(string? accountId) => AccountId = accountId;

    public void UpdateDetails(string name, string code, int priority,
        decimal costPerInput, decimal costPerOutput, int? maxTokens)
    {
        Name = name;
        Code = code.ToLowerInvariant();
        Priority = priority;
        CostPerInputToken = costPerInput;
        CostPerOutputToken = costPerOutput;
        MaxTokensPerRequest = maxTokens;
    }

    /// <summary>
    /// Switches the provider's auth method. When switching to OAuth, the static
    /// API key is cleared (the gateway brokers the credential instead). When
    /// switching back to ApiKey, any OAuth linkage is dropped (token rows are
    /// revoked out-of-band by the caller/flow service).
    /// </summary>
    public void SetAuthMethod(AuthMethod method, Guid? oauthConfigId = null)
    {
        if (method == AuthMethod.OAuth)
        {
            if (oauthConfigId is null)
                throw new ArgumentNullException(nameof(oauthConfigId));
            AuthMethod = AuthMethod.OAuth;
            OAuthConfigId = oauthConfigId;
            ApiKey = null; // OAuth supersedes a static key
        }
        else
        {
            AuthMethod = AuthMethod.ApiKey;
            OAuthConfigId = null;
        }
    }

    /// <summary>
    /// Replaces the sealed credential. <paramref name="apiKeyPlaintext"/> is the
    /// plaintext form from the user/admin — it MUST be sealed by the vault before
    /// being stored. The new BaseUrl (if any) is validated against SSRF defenses.
    /// </summary>
    /// <param name="validator">
    /// Required when <paramref name="baseUrl"/> is provided. Pass null only when
    /// the caller is migrating legacy data and explicitly opts out of SSRF.
    /// </param>
    /// <param name="vault">
    /// Required when <paramref name="apiKeyPlaintext"/> is provided. Pass null
    /// only for BaseUrl-only updates; plaintext without a vault is a hard error.
    /// </param>
    /// <param name="validator">
    /// Required when <paramref name="baseUrl"/> is provided (default null for
    /// BaseUrl-only calls). The merged signature supports the API-key-only
    /// admin path (no URL change) without forcing callers to pass a validator.
    /// </param>
    public void UpdateCredentials(string? baseUrl, string? apiKeyPlaintext,
        ICredentialVault? vault, UrlSafetyValidator? validator = null)
    {
        // SSRF defense on URL update
        if (baseUrl is not null)
        {
            if (validator is null)
                throw new InvalidOperationException(
                    "UpdateCredentials requires a UrlSafetyValidator when changing the BaseUrl.");
            BaseUrl = validator.ValidateLiteral(baseUrl).ToString().TrimEnd('/');
        }

        // Vault sealing on credential update
        if (vault is not null)
        {
            if (apiKeyPlaintext is not null)
                ApiKey = vault.Seal(apiKeyPlaintext);
        }
        else
        {
            // No vault — refuse to accept a plaintext credential.
            if (apiKeyPlaintext is not null)
                throw new InvalidOperationException(
                    "Cannot update provider credentials with a plaintext API key " +
                    "without an ICredentialVault. Configure a vault first.");
        }
    }

    /// <summary>
    /// Returns the decrypted API key (plaintext) for use in outbound HTTP calls.
    /// Returns null when no key is configured. Throws on vault/ciphertext errors.
    /// The returned value is intended for immediate use; do not retain references.
    /// </summary>
    public string? DecryptApiKey(ICredentialVault vault)
    {
        if (string.IsNullOrEmpty(ApiKey)) return null;
        return vault.Open(ApiKey);
    }

    /// <summary>
    /// Indicates whether this provider has a configured credential (sealed or otherwise).
    /// Does NOT decrypt — use <see cref="DecryptApiKey"/> for that.
    /// </summary>
    public bool HasCredential => !string.IsNullOrEmpty(ApiKey);

    /// <summary>
    /// True when the stored <see cref="ApiKey"/> is in the envelope-encrypted format
    /// (starts with the "v1:" version tag). False for legacy plaintext rows that
    /// were stored before envelope encryption was introduced.
    /// </summary>
    public bool IsApiKeySealed => ApiKey?.StartsWith("v1:", StringComparison.Ordinal) == true;
}
