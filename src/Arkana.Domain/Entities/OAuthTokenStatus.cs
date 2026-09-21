namespace Arkana.Domain.Entities;

/// <summary>Lifecycle state of a per-provider OAuth connection.</summary>
public enum OAuthTokenStatus
{
    /// <summary>Flow started, awaiting user authorization + token exchange.</summary>
    Pending = 0,

    /// <summary>Tokens exchanged and stored; bearer usable.</summary>
    Connected = 1,

    /// <summary>Access token past expiry and refresh unavailable.</summary>
    Expired = 2,

    /// <summary>Last exchange/refresh failed irrecoverably.</summary>
    Error = 3,

    /// <summary>Admin disconnected; tokens cleared.</summary>
    Revoked = 4
}
