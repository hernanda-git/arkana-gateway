using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces;

public sealed record ProviderAccountSelectionRequest(
    Guid TenantId,
    Guid ProviderId,
    string Model,
    AccountRoutingMode Mode = AccountRoutingMode.Pool,
    string? PinnedAccountCode = null,
    DateTimeOffset? At = null,
    bool BrokerManagedOnly = false);

public sealed record ProviderAccountSelectionResult(
    ProviderAccount? Account,
    IReadOnlyList<ProviderAccount> EligibleAccounts,
    string? RejectionReason)
{
    public bool HasAccount => Account is not null;
}

public interface IProviderAccountSelector
{
    Task<ProviderAccountSelectionResult> SelectAsync(ProviderAccountSelectionRequest request, CancellationToken ct = default);
}

public static class ProviderAccountEligibility
{
    public static bool IsEligible(ProviderAccount account, string model, DateTimeOffset at)
        => account.IsHealthy(at) && account.SupportsModel(model);
}

/// <summary>Explicit transport family selected for one immutable request target.</summary>
public enum ProviderRouteKind
{
    Generic = 0,
    NativeGemini = 1,
    BrokerManagedGemini = 2,
    DirectCloudCode = 3,
}

/// <summary>
/// Durable provider/account resolution result. A Gemini account code is only an
/// input selector; ownership comes from the tenant-scoped ProviderAccount row.
/// </summary>
public sealed record ProviderTarget(
    Guid TenantId,
    Guid ProviderId,
    string RequestedProviderCode,
    string ModelRequested,
    ProviderRouteKind RouteKind,
    Guid? ProviderAccountId = null,
    string? AccountCode = null,
    string? BrokerInstanceId = null,
    string? RejectionReason = null)
{
    public bool IsResolved => RejectionReason is null;
}

public interface IProviderTargetPlanner
{
    Task<ProviderTarget> ResolveAsync(
        Guid tenantId,
        AiProvider provider,
        string model,
        Guid? requestedAccountId = null,
        CancellationToken ct = default);
}
