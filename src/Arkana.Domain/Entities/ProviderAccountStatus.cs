namespace Arkana.Domain.Entities;

/// <summary>Durable control-plane state for a provider account.</summary>
public enum ProviderAccountStatus
{
    Pending = 0,
    Connected = 1,
    Draining = 2,
    Disabled = 3,
    ReconnectRequired = 4,
    OutOfSync = 5,
    MissingCredential = 6
}
