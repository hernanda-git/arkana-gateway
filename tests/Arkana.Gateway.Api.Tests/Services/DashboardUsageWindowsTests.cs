using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Gateway.Api.Endpoints;
using Arkana.Gateway.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Arkana.Gateway.Api.Tests.Services;

/// <summary>
/// Tests for DashboardService.GetUsageWindowsAsync — the profile-page usage
/// windows: only accounts pinned by the caller's own keys surface in both
/// "Pinned" and "Pool"; no snapshots at all => HasData=false.
/// </summary>
public sealed class DashboardUsageWindowsTests
{
    private const string Window = "5h";
    private static readonly Guid Tenant = ProviderAccountDashboardFacade.DefaultTenantId;

    private static (DashboardService Svc, IApiKeyRepository Keys, IAccountUsageSnapshotRepository Snapshots) Create(
        List<ApiKey>? keys = null, List<AccountUsageSnapshot>? snapshots = null)
    {
        var keyRepo = Substitute.For<IApiKeyRepository>();
        keyRepo.GetAllAsync().Returns(keys ?? []);
        keyRepo.GetAllAsync(Tenant, Arg.Any<CancellationToken>()).Returns(keys ?? []);

        var snapRepo = Substitute.For<IAccountUsageSnapshotRepository>();
        snapRepo.GetByAccountCodesAsync(
                Arg.Any<Guid>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var requested = callInfo.Arg<IEnumerable<string>?>();
                var source = snapshots ?? [];
                if (requested is null)
                    return source;
                var codes = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
                return source.Where(s => codes.Contains(s.AccountCode)).ToList();
            });

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(Tenant);

        var svc = new DashboardService(
            Substitute.For<IAiProviderRepository>(),
            keyRepo,
            Substitute.For<IModelRepository>(),
            Substitute.For<ITokenTracker>(),
            Substitute.For<IRequestLogger>(),
            new ActiveStreamCounter(),
            Substitute.For<IN8nService>(),
            new UserTimeService(Substitute.For<IConfiguration>()),
            Substitute.For<ICredentialVault>(),
            new UrlSafetyValidator(new UrlSafetyOptions { AllowHttp = true, AllowPrivateAddresses = true }, new DnsResolver()),
            Substitute.For<System.Net.Http.IHttpClientFactory>(),
            Substitute.For<IApiKeyPoolRepository>(),
            Substitute.For<IAgentRepository>(),
            new AgentOrchestrator(
                Substitute.For<IAgentRepository>(),
                Substitute.For<IModelRepository>(),
                Substitute.For<IAiProviderRepository>(),
                Substitute.For<Arkana.Domain.Interfaces.Canonical.IProviderConnectorFactory>(),
                Substitute.For<ILogger<AgentOrchestrator>>()),
            Substitute.For<IOAuthFlowService>(),
            Substitute.For<IOAuthProviderConfigRepository>(),
            Substitute.For<Microsoft.AspNetCore.Components.NavigationManager>(),
            snapRepo,
            tenantProvider: tenantProvider);
        return (svc, keyRepo, snapRepo);
    }

    private static ApiKey OwnedKey(Guid owner, string name, string? pin)
    {
        var key = ApiKey.Create(name, $"hash-{name}", "arkana-test", Tenant);
        key.BindToUser(owner);
        key.PreferredProviderCode = pin;
        return key;
    }

    private static AccountUsageSnapshot Snapshot(string code, UsageWindowKind kind, double pct) =>
        new(Guid.NewGuid(), code, kind, pct, kind == UsageWindowKind.Primary ? 300 : 10080,
            DateTimeOffset.UtcNow.AddHours(3), "plus");

    [Fact]
    public async Task Pinned_Windows_Only_For_Owner_Key_Pins()
    {
        var owner = Guid.NewGuid();
        var keys = new List<ApiKey>
        {
            OwnedKey(owner, "k1", "chatgpt-acc2"),
            // Someone else's pinned key must NOT leak into this owner's view.
            OwnedKey(Guid.NewGuid(), "other", "chatgpt-acc4"),
        };
        var snaps = new List<AccountUsageSnapshot>
        {
            Snapshot("chatgpt-acc2", UsageWindowKind.Primary, 40),
            Snapshot("chatgpt-acc2", UsageWindowKind.Secondary, 10),
            Snapshot("chatgpt-acc4", UsageWindowKind.Primary, 90),
        };

        var (svc, _, _) = Create(keys, snaps);
        var result = await svc.GetUsageWindowsAsync(owner);

        result.HasData.Should().BeTrue();
        result.Pinned.Select(w => w.AccountCode).Should().OnlyContain(c => c == "chatgpt-acc2");
        result.Pinned.Should().HaveCount(2);
        result.Pinned.Single(w => w.Window == Window).UsedPercent.Should().Be(40);

        // A different user's account must not leak through the pool overview.
        result.Pool.Select(a => a.AccountCode).Should().BeEquivalentTo("chatgpt-acc2");
    }

    [Fact]
    public async Task Unpinned_Owner_Has_No_Usage_Pool()
    {
        var owner = Guid.NewGuid();
        var keys = new List<ApiKey> { OwnedKey(owner, "nopin", null) };
        var snaps = new List<AccountUsageSnapshot>
        {
            Snapshot("chatgpt-acc1", UsageWindowKind.Primary, 5),
        };

        var (svc, _, _) = Create(keys, snaps);
        var result = await svc.GetUsageWindowsAsync(owner);

        result.Pinned.Should().BeEmpty();
        result.Pool.Should().BeEmpty();
    }

    [Fact]
    public async Task No_Snapshots_HasData_Is_False()
    {
        var owner = Guid.NewGuid();
        var (svc, _, _) = Create([OwnedKey(owner, "k", "chatgpt-acc1")], []);
        var result = await svc.GetUsageWindowsAsync(owner);

        result.HasData.Should().BeFalse();
        result.Pinned.Should().BeEmpty();
        result.Pool.Should().BeEmpty();
    }

    [Fact]
    public async Task Staleness_Flag_Set_Beyond_One_Hour()
    {
        var owner = Guid.NewGuid();
        var fresh = Snapshot("chatgpt-acc2", UsageWindowKind.Primary, 20);
        var stale = new AccountUsageSnapshot(
            Guid.NewGuid(), "chatgpt-acc2", UsageWindowKind.Secondary, 20, 10080, null, null);
        typeof(AccountUsageSnapshot)
            .GetProperty(nameof(AccountUsageSnapshot.UpdatedAtUtc))!
            .SetValue(stale, DateTimeOffset.UtcNow.AddHours(-3));

        var (svc, _, _) = Create([OwnedKey(owner, "k", "chatgpt-acc2")], [fresh, stale]);
        var result = await svc.GetUsageWindowsAsync(owner);

        result.Pinned.Single(w => w.Window == Window).IsStale.Should().BeFalse();
        result.Pinned.Single(w => w.Window == "week").IsStale.Should().BeTrue();
    }

    [Fact]
    public void ResolveOwnerUserId_Parses_NameIdentifier_Claim()
    {
        var id = Guid.NewGuid();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, id.ToString()),
        };
        http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims));

        ProfileEndpoints.ResolveOwnerUserId(http).Should().Be(id);
    }
}
