using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;

namespace Arkana.Infrastructure.OAuth;

/// <summary>
/// Resolves a live bearer token for the connector layer. Returns null for
/// API-key providers so the connector falls back to <see cref="AiProvider.DecryptApiKey"/>.
/// </summary>
public sealed class OAuthTokenResolver : IOAuthTokenResolver
{
    private readonly IOAuthFlowService _flow;
    public OAuthTokenResolver(IOAuthFlowService flow) => _flow = flow;

    public Task<string?> GetBearerTokenAsync(Guid providerId, CancellationToken ct = default)
        => _flow.GetValidAccessTokenAsync(providerId, refreshSlack: TimeSpan.FromMinutes(5), ct: ct);

    public Task<string?> GetBearerTokenAsync(Guid providerId, Guid tenantId, CancellationToken ct = default)
        => _flow.GetValidAccessTokenForTenantAsync(providerId, tenantId, refreshSlack: TimeSpan.FromMinutes(5), ct: ct);
}
