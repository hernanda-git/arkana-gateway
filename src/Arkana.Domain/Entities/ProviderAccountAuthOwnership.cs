namespace Arkana.Domain.Entities;

/// <summary>Identifies who owns and refreshes the upstream OAuth credential.</summary>
public enum ProviderAccountAuthOwnership
{
    GatewayManagedOAuth = 0,
    BrokerManagedOAuth = 1
}
