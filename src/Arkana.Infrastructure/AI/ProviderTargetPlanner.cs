using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Resolves the provider family from durable tenant/account ownership.
/// Prefix aliases are accepted only as lookup input and never authorize a
/// broker or native transport by themselves.
/// </summary>
public sealed class ProviderTargetPlanner : IProviderTargetPlanner
{
    private readonly IProviderAccountRepository _accounts;

    public ProviderTargetPlanner(IProviderAccountRepository accounts)
        => _accounts = accounts;

    public async Task<ProviderTarget> ResolveAsync(
        Guid tenantId,
        AiProvider provider,
        string model,
        Guid? requestedAccountId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (tenantId == Guid.Empty)
            return Rejected(tenantId, provider, model, "Authenticated tenant is required.");

        var code = provider.Code.Trim();
        if (code.Equals("gemini", StringComparison.OrdinalIgnoreCase))
        {
            if (requestedAccountId is null)
                return Native(tenantId, provider, model);

            var nativeAccount = await _accounts.GetByIdAsync(tenantId, requestedAccountId.Value, ct);
            if (nativeAccount is null || nativeAccount.AiProviderId != provider.Id
                || nativeAccount.AuthOwnership != ProviderAccountAuthOwnership.GatewayManagedOAuth
                || !provider.UsesOAuth)
                return Rejected(tenantId, provider, model, "Native Gemini account ownership is invalid.");

            return Native(tenantId, provider, model, nativeAccount);
        }

        if (code.Equals("gemini-subscription", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderTarget(tenantId, provider.Id, code, model,
                ProviderRouteKind.BrokerManagedGemini);
        }

        if (!code.StartsWith("gemini-acc", StringComparison.OrdinalIgnoreCase))
            return new ProviderTarget(tenantId, provider.Id, code, model, ProviderRouteKind.Generic);

        var account = requestedAccountId is { } accountId
            ? await _accounts.GetByIdAsync(tenantId, accountId, ct)
            : await _accounts.GetByCodeAsync(tenantId, code, ct);

        if (account is null || account.AiProviderId != provider.Id
            || !string.Equals(account.Code, code, StringComparison.OrdinalIgnoreCase))
            return Rejected(tenantId, provider, model, "Gemini account ownership is missing or mismatched.");

        if (account.AuthOwnership == ProviderAccountAuthOwnership.BrokerManagedOAuth
            && account.BrokerKind == BrokerKind.CLIProxyAPI
            && !string.IsNullOrWhiteSpace(account.BrokerInstanceId))
        {
            return new ProviderTarget(tenantId, provider.Id, code, model,
                ProviderRouteKind.BrokerManagedGemini, account.Id, account.Code,
                account.BrokerInstanceId);
        }

        if (account.AuthOwnership == ProviderAccountAuthOwnership.GatewayManagedOAuth
            && provider.UsesOAuth)
        {
            return Native(tenantId, provider, model, account);
        }

        return Rejected(tenantId, provider, model, "Gemini account authentication ownership is unsupported.");
    }

    private static ProviderTarget Native(Guid tenantId, AiProvider provider, string model, ProviderAccount? account = null)
        => new(tenantId, provider.Id, provider.Code, model, ProviderRouteKind.NativeGemini,
            account?.Id, account?.Code);

    private static ProviderTarget Rejected(Guid tenantId, AiProvider provider, string model, string reason)
        => new(tenantId, provider.Id, provider.Code, model, ProviderRouteKind.Generic,
            RejectionReason: reason);
}