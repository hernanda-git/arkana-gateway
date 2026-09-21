using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.Broker;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Entities;
using Arkana.Gateway.Api.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Arkana.Gateway.Api.Tests.Services;

public sealed class ProviderAccountOperationRecoveryTests
{
    [Fact]
    public async Task ExpiredOperationIsRecoveredBeforeDifferentKeyClaims()
    {
        var tenant = ProviderAccountDashboardFacade.DefaultTenantId;
        var provider = AiProvider.Create("Gemini", "gemini", 1);
        var account = ProviderAccount.Create(tenant, provider.Id, "acct", "Account", brokerInstanceId: "slot");
        var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.AiProviders.Add(provider);
        db.ProviderAccounts.Add(account);
        db.ProviderAccountOperations.Add(new ProviderAccountOperation
        {
            Id = Guid.NewGuid(), TenantId = tenant, ProviderAccountId = account.Id, Kind = ProviderAccountOperationKind.Start,
            State = ProviderAccountOperationState.Running, Nonce = "nonce", IdempotencyKey = "old-key", RequestFingerprint = "old",
            Slot = "slot", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20), UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-20), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();

        var accounts = Substitute.For<IProviderAccountRepository>();
        accounts.GetByIdAsync(tenant, account.Id, Arg.Any<CancellationToken>()).Returns(account);
        accounts.TryReserveVersionAsync(tenant, account.Id, account.Version, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                typeof(ProviderAccount).GetProperty(nameof(ProviderAccount.Version))!.SetValue(account, callInfo.ArgAt<Guid>(3));
                return true;
            });
        var client = Substitute.For<ICLIProxyManagementClient>();
        client.StartOAuthAsync(Arg.Any<CancellationToken>()).Returns(new CLIProxyOAuthStart("slot", "https://example.test/auth", DateTimeOffset.UtcNow.AddMinutes(5)));
        var clients = Substitute.For<ICLIProxyManagementClientFactory>(); clients.Create("slot").Returns(client);
        var tenantProvider = Substitute.For<ITenantProvider>(); tenantProvider.TenantId.Returns(tenant);
        var facade = new ProviderAccountDashboardFacade(accounts, clients, db, tenantProvider);

        var result = await facade.StartAsync(account.Id, "slot", false, "new-key", account.Version, CancellationToken.None);

        result.AuthorizationUrl.Should().Be("https://example.test/auth");
        (await db.ProviderAccountOperations.SingleAsync(x => x.IdempotencyKey == "old-key")).State.Should().Be(ProviderAccountOperationState.Expired);
        await client.Received(1).StartOAuthAsync(Arg.Any<CancellationToken>());
    }
}