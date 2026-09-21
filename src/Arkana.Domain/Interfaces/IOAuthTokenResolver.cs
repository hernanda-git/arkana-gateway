using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Resolves a live bearer token for a provider. The connector layer depends
/// on this abstraction (not on <c>IOAuthFlowService</c>) so it stays
/// unit-testable. For API-key providers it returns null and the connector
/// falls back to <see cref="AiProvider.DecryptApiKey"/>.
/// </summary>
public interface IOAuthTokenResolver
{
    /// <summary>
    /// Returns a usable (auto-refreshed) OAuth access token for the provider,
    /// or null when the provider is not OAuth-authenticated or has no valid token.
    /// </summary>
    Task<string?> GetBearerTokenAsync(Guid providerId, CancellationToken ct = default);
    Task<string?> GetBearerTokenAsync(Guid providerId, Guid tenantId, CancellationToken ct = default);
}
