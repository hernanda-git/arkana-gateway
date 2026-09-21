using Arkana.Domain.Interfaces;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// Default tenant provider that always returns the default tenant.
/// Used as a placeholder until the full tenant resolution from
/// API keys or dashboard users is wired up.
/// </summary>
internal sealed class DefaultTenantProvider : ITenantProvider
{
    private static readonly Guid DefaultTenantId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    public Guid? TenantId => DefaultTenantId;
}
