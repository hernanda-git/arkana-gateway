namespace Arkana.Infrastructure.Persistence.Entities;

public enum ProviderAccountOperationKind { Start, Cancel, Status, Reconnect, Probe, Callback, Enable, Disable, Drain, Delete }
public enum ProviderAccountOperationState { Pending, Running, Succeeded, Failed, Cancelled, Expired, Indeterminate }

public sealed class ProviderAccountOperation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProviderAccountId { get; set; }
    public ProviderAccountOperationKind Kind { get; set; }
    public ProviderAccountOperationState State { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public Guid? ExpectedVersion { get; set; }
    public string? AuthorizationUrl { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Slot { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
