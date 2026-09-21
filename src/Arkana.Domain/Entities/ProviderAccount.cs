namespace Arkana.Domain.Entities;

/// <summary>
/// Gateway control-plane identity for one upstream provider account. Broker-managed
/// OAuth credentials remain in the broker; this aggregate stores only safe references.
/// </summary>
public sealed class ProviderAccount
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AiProviderId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public ProviderAccountAuthOwnership AuthOwnership { get; private set; }
    public BrokerKind? BrokerKind { get; private set; }
    public string? BrokerCredentialId { get; private set; }
    public string? ExternalCredentialFileName { get; private set; }
    public string? BrokerInstanceId { get; private set; }
    public string? AuthDirectoryKey { get; private set; }
    public string? RoutingPrefix { get; private set; }
    /// <summary>Durable comma-separated model allowlist. Empty means all models for this provider.</summary>
    public string? SupportedModels { get; private set; }
    public bool IsEnabled { get; private set; }
    public ProviderAccountStatus ConnectionStatus { get; private set; }
    public DateTimeOffset? CooldownUntil { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public DateTimeOffset? LastFailureAt { get; private set; }
    public string? LastFailureClass { get; private set; }
    public DateTimeOffset? LastBrokerSyncAt { get; private set; }
    public DateTimeOffset? TokenExpiresAt { get; private set; }
    public Guid Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public string? AuditActor { get; private set; }

    private ProviderAccount() { }

    public static ProviderAccount Create(Guid tenantId, Guid aiProviderId, string code, string displayName,
        ProviderAccountAuthOwnership authOwnership = ProviderAccountAuthOwnership.BrokerManagedOAuth,
        BrokerKind? brokerKind = global::Arkana.Domain.Entities.BrokerKind.CLIProxyAPI, string? brokerCredentialId = null,
        string? brokerInstanceId = null, string? authDirectoryKey = null)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        if (aiProviderId == Guid.Empty) throw new ArgumentException("Provider id is required.", nameof(aiProviderId));
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Account code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Display name is required.", nameof(displayName));
        if (authDirectoryKey is not null) ValidateAuthDirectoryKey(authDirectoryKey);
        if (authOwnership == ProviderAccountAuthOwnership.BrokerManagedOAuth && brokerKind is null)
            throw new ArgumentException("Broker kind is required for broker-managed OAuth.", nameof(brokerKind));

        var now = DateTimeOffset.UtcNow;
        return new ProviderAccount
        {
            Id = Guid.NewGuid(), TenantId = tenantId, AiProviderId = aiProviderId,
            Code = code.Trim().ToLowerInvariant(), DisplayName = displayName.Trim(),
            AuthOwnership = authOwnership, BrokerKind = brokerKind, BrokerCredentialId = brokerCredentialId,
            BrokerInstanceId = brokerInstanceId, AuthDirectoryKey = authDirectoryKey,
            IsEnabled = true, ConnectionStatus = ProviderAccountStatus.Pending,
            Version = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now
        };
    }

    public bool IsHealthy(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        return IsEnabled && ConnectionStatus == ProviderAccountStatus.Connected &&
               (!CooldownUntil.HasValue || CooldownUntil <= at) && DeletedAt is null;
    }

    public bool SupportsModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(SupportedModels)) return true;
        return SupportedModels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => x.Equals(model, StringComparison.OrdinalIgnoreCase));
    }

    public void SetSupportedModels(IEnumerable<string>? models)
    {
        var values = models?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        SupportedModels = values.Length == 0 ? null : string.Join(',', values);
        Touch();
    }

    public void Enable() { IsEnabled = true; ConnectionStatus = ProviderAccountStatus.Connected; Touch(); }
    public void Disable() { IsEnabled = false; ConnectionStatus = ProviderAccountStatus.Disabled; Touch(); }
    public void BeginDrain() { ConnectionStatus = ProviderAccountStatus.Draining; Touch(); }
    public void MarkConnected(DateTimeOffset? tokenExpiresAt = null) { IsEnabled = true; ConnectionStatus = ProviderAccountStatus.Connected; TokenExpiresAt = tokenExpiresAt; CooldownUntil = null; Touch(); }
    public void MarkPending() { IsEnabled = true; ConnectionStatus = ProviderAccountStatus.Pending; Touch(); }
    public void MarkReconnectRequired() { ConnectionStatus = ProviderAccountStatus.ReconnectRequired; Touch(); }
    public void BindBrokerCredential(string stableAuthId)
    {
        if (string.IsNullOrWhiteSpace(stableAuthId))
            throw new ArgumentException("Broker credential reference is required.", nameof(stableAuthId));
        if (stableAuthId.Length > 512)
            throw new ArgumentException("Broker credential reference is too long.", nameof(stableAuthId));
        BrokerCredentialId = stableAuthId.Trim();
        Touch();
    }
    public void MarkOutOfSync() { IsEnabled = false; ConnectionStatus = ProviderAccountStatus.OutOfSync; Touch(); }
    public void MarkMissingCredential() { IsEnabled = false; ConnectionStatus = ProviderAccountStatus.MissingCredential; Touch(); }
    public void RecordSuccess(DateTimeOffset? at = null) { LastSuccessAt = at ?? DateTimeOffset.UtcNow; LastFailureClass = null; Touch(); }
    public void RecordFailure(string failureClass, DateTimeOffset? at = null) { LastFailureClass = string.IsNullOrWhiteSpace(failureClass) ? throw new ArgumentException("Failure class is required.", nameof(failureClass)) : failureClass.Trim(); LastFailureAt = at ?? DateTimeOffset.UtcNow; Touch(); }
    public void SetCooldown(DateTimeOffset until, string? failureClass = null) { CooldownUntil = until; if (failureClass is not null) RecordFailure(failureClass); else Touch(); }
    public void ClearCooldown() { CooldownUntil = null; Touch(); }
    public void SyncFromBroker(DateTimeOffset? at = null) { LastBrokerSyncAt = at ?? DateTimeOffset.UtcNow; Touch(); }
    public void Tombstone(string? actor = null) { IsEnabled = false; DeletedAt = DateTimeOffset.UtcNow; AuditActor = actor; ConnectionStatus = ProviderAccountStatus.Disabled; Touch(); }

    private void Touch() { UpdatedAt = DateTimeOffset.UtcNow; Version = Guid.NewGuid(); }
    private static void ValidateAuthDirectoryKey(string key)
    {
        if (key.Length > 128 || key.StartsWith('/') || key.StartsWith('\\') || key.Contains("..", StringComparison.Ordinal) || key.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("Auth directory key must be a safe relative key.", nameof(key));
    }
}
