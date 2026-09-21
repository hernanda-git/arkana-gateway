using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;

namespace Arkana.Infrastructure.AI;

/// <summary>Deterministic account selection over durable control-plane state.</summary>
internal sealed class ProviderAccountSelector : IProviderAccountSelector
{
    private readonly IProviderAccountRepository _repository;
    public ProviderAccountSelector(IProviderAccountRepository repository) => _repository = repository;

    public async Task<ProviderAccountSelectionResult> SelectAsync(ProviderAccountSelectionRequest request, CancellationToken ct = default)
    {
        if (request.TenantId == Guid.Empty || request.ProviderId == Guid.Empty)
            throw new ArgumentException("Tenant and provider are required.");
        var all = await _repository.GetForProviderAsync(request.TenantId, request.ProviderId, false, ct);
        var at = request.At ?? DateTimeOffset.UtcNow;
        var eligible = all.Where(x => ProviderAccountEligibility.IsEligible(x, request.Model, at)
            && (!request.BrokerManagedOnly || x.AuthOwnership == ProviderAccountAuthOwnership.BrokerManagedOAuth))
            .OrderBy(x => x.Code, StringComparer.Ordinal).ToArray();
        ProviderAccount? pinned = null;
        if (!string.IsNullOrWhiteSpace(request.PinnedAccountCode))
            pinned = all.FirstOrDefault(x => x.Code.Equals(request.PinnedAccountCode.Trim(), StringComparison.OrdinalIgnoreCase));

        if (request.Mode == AccountRoutingMode.StrictPin)
        {
            if (pinned is null) return new(null, eligible, "Pinned account does not exist.");
            return ProviderAccountEligibility.IsEligible(pinned, request.Model, at)
                && (!request.BrokerManagedOnly || pinned.AuthOwnership == ProviderAccountAuthOwnership.BrokerManagedOAuth)
                ? new(pinned, eligible, null)
                : new(null, eligible, "Pinned account is not eligible.");
        }
        if (request.Mode == AccountRoutingMode.PinWithSameProviderFailover && pinned is not null && ProviderAccountEligibility.IsEligible(pinned, request.Model, at))
            return new(pinned, eligible, null);
        if (request.Mode == AccountRoutingMode.PinWithSameProviderFailover && !string.IsNullOrWhiteSpace(request.PinnedAccountCode) && pinned is null)
            return new(null, eligible, "Pinned account does not exist.");
        return eligible.Length == 0 ? new(null, eligible, "No eligible provider accounts.") : new(eligible[0], eligible, null);
    }
}
