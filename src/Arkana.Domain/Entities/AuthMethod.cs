namespace Arkana.Domain.Entities;

/// <summary>
/// How a provider authenticates to its upstream. Drives whether the
/// connector injects a static API key or a live OAuth bearer token.
/// </summary>
public enum AuthMethod
{
    /// <summary>Static API key stored sealed in <see cref="AiProvider.ApiKey"/>.</summary>
    ApiKey = 0,

    /// <summary>
    /// Interactive OAuth (device-code / authorization-code). The gateway
    /// brokers the token, stores it sealed, and auto-refreshes it.
    /// </summary>
    OAuth = 1
}
