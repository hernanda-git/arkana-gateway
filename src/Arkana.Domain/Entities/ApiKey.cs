namespace Arkana.Domain.Entities;

/// <summary>
/// Registered API key for authentication, scoped to specific models.
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; private set; }
    public string KeyHash { get; private set; } = string.Empty;
    /// <summary>
    /// First 12 characters of the raw API key — shown in UI so users can identify keys.
    /// Only stored at creation time; the full raw key is never persisted.
    /// </summary>
    public string KeyPrefix { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    // Per-key rate limit overrides (null = use global default)
    public int? RateLimitRpm { get; set; }
    public int? RateLimitTpm { get; set; }
    public int? RateLimitMaxConcurrent { get; set; }

    // Multi-tenant scoping (ENT-ARKANA-001)
    public Guid TenantId { get; private set; }
    public Tenant Tenant { get; private set; } = null!;

    /// <summary>Dashboard user that owns this key, when it is profile-bound.</summary>
    public Guid? OwnerUserId { get; private set; }
    public DashboardUser? OwnerUser { get; private set; }

    // Navigation — which models this key is allowed to access
    public ICollection<Model> AllowedModels { get; private set; } = [];

    /// <summary>
    /// Optional provider code (e.g. "ollama") that this key's requests should
    /// be routed to, overriding the model's own provider.
    /// Null = normal routing (the provider that owns the requested model).
    /// </summary>
    /// <remarks>
    /// Added 2026-08-06. Previously the only way to pin a client to a
    /// particular upstream was to restrict <see cref="AllowedModels"/> to
    /// models owned by that provider, which conflates "what may this client
    /// use" with "where should it run". This allows per-client routing —
    /// e.g. pointing one tenant at the self-hosted Ollama upstream while
    /// everyone else stays on the primary.
    /// </remarks>
    public string? PreferredProviderCode { get; set; }

    /// <summary>
    /// Explicit opt-in for crossing the configured provider boundary when the
    /// preferred provider is unavailable. False is fail-closed isolation.
    /// </summary>
    public bool AllowProviderFallback { get; set; }

    /// <summary>Optional account pin inside the preferred provider.</summary>
    public Guid? PreferredProviderAccountId { get; set; }

    /// <summary>Controls pool versus strict/same-provider account routing.</summary>
    public AccountRoutingMode AccountRoutingMode { get; set; } = AccountRoutingMode.Pool;

    /// <summary>Explicit opt-in to fail over between accounts of one provider.</summary>
    public bool AllowAccountFallback { get; set; }

    private ApiKey() { } // EF Core

    public static ApiKey Create(string name, string keyHash, string keyPrefix, Guid tenantId, DateTimeOffset? expiresAt = null)
    {
        return new ApiKey
        {
            Id = Guid.NewGuid(),
            KeyHash = keyHash,
            KeyPrefix = keyPrefix,
            Name = name,
            TenantId = tenantId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt
        };
    }

    /// <summary>
    /// Creates an API key without a tenant (legacy single-tenant mode).
    /// Called from tests and pre-migration code paths.
    /// </summary>
    public static ApiKey Create(string name, string keyHash, string keyPrefix, DateTimeOffset? expiresAt = null)
        => Create(name, keyHash, keyPrefix, Guid.Parse("00000000-0000-0000-0000-000000000001"), expiresAt);

    public bool IsExpired() => ExpiresAt.HasValue && ExpiresAt.Value < DateTimeOffset.UtcNow;
    public void Rename(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Key name cannot be empty", nameof(newName));
        Name = newName.Trim();
    }

    public void Deactivate() => IsActive = false;
    public void Activate() => IsActive = true;

    public void BindToUser(Guid userId)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("Owner user id is required", nameof(userId));
        if (OwnerUserId.HasValue && OwnerUserId.Value != userId)
            throw new InvalidOperationException("This API key is already bound to another user.");
        OwnerUserId = userId;
    }

    public void UnbindFromUser(Guid userId)
    {
        if (OwnerUserId == userId)
            OwnerUserId = null;
    }

    /// <summary>
    /// Regenerate the secret for this key — replaces the stored hash and prefix
    /// with a freshly generated key. The previous key immediately stops working.
    /// Any client holding the old key must be updated.
    /// </summary>
    public void Rotate(string keyHash, string keyPrefix)
    {
        if (string.IsNullOrWhiteSpace(keyHash))
            throw new ArgumentException("keyHash is required", nameof(keyHash));
        if (string.IsNullOrWhiteSpace(keyPrefix))
            throw new ArgumentException("keyPrefix is required", nameof(keyPrefix));
        KeyHash = keyHash;
        KeyPrefix = keyPrefix;
    }

    public bool CanAccessModel(Guid modelId) =>
        AllowedModels.Count == 0 || AllowedModels.Any(m => m.Id == modelId);
}
