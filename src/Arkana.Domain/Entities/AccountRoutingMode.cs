namespace Arkana.Domain.Entities;

/// <summary>Account selection behavior inside the preferred provider boundary.</summary>
public enum AccountRoutingMode
{
    Pool = 0,
    StrictPin = 1,
    PinWithSameProviderFailover = 2
}
