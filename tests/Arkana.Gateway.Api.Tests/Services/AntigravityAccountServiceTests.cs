using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Gateway.Api.Services;
using Arkana.Infrastructure.Broker;
using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Arkana.Gateway.Api.Tests.Services;

public sealed class AntigravityAccountServiceTests
{
    [Fact]
    public async Task CreateAsync_CreatesBrokerProviderModelAndAccountProjection()
    {
        var tenantId = Guid.NewGuid();
        var (db, dbFactory) = CreateDb();
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        clients.Create("gemini-broker-a").Returns(Substitute.For<ICLIProxyManagementClient>());
        var service = CreateService(db, dbFactory, tenant, clients);

        var result = await service.CreateAsync("Antigravity One");

        result.Code.Should().Be("gemini-acc1");
        result.DisplayName.Should().Be("Antigravity One");
        result.Slot.Should().Be("gemini-broker-a");
        result.Status.Should().Be(ProviderAccountStatus.Pending.ToString());

        var provider = await db.AiProviders.SingleAsync();
        provider.Code.Should().Be("gemini-acc1");
        provider.Name.Should().Be("Antigravity One");
        provider.AuthMethod.Should().Be(AuthMethod.ApiKey);

        var models = await db.Models.Where(x => x.ProviderId == provider.Id).ToListAsync();
        models.Select(x => x.Code).Should().Contain("gemini-3-flash");
        models.Select(x => x.Code).Should().Contain("claude-sonnet-4-6");

        var account = await db.ProviderAccounts.SingleAsync();
        account.TenantId.Should().Be(tenantId);
        account.AiProviderId.Should().Be(provider.Id);
        account.Code.Should().Be("gemini-acc1");
        account.AuthOwnership.Should().Be(ProviderAccountAuthOwnership.BrokerManagedOAuth);
        account.BrokerKind.Should().Be(BrokerKind.CLIProxyAPI);
        account.BrokerInstanceId.Should().Be("gemini-broker-a");
        account.SupportedModels.Should().Contain("gemini-3-flash");
    }

    [Fact]
    public async Task CreateAsync_SkipsExistingAccountCodes()
    {
        var tenantId = Guid.NewGuid();
        var (db, dbFactory) = CreateDb();
        var existing = AiProvider.Create("Existing", "gemini-acc1", 101);
        existing.AssignTenant(tenantId);
        db.AiProviders.Add(existing);
        await db.SaveChangesAsync();

        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        clients.Create("gemini-broker-a").Returns(Substitute.For<ICLIProxyManagementClient>());
        var service = CreateService(db, dbFactory, tenant, clients);

        var result = await service.CreateAsync("Antigravity Two");

        result.Code.Should().Be("gemini-acc2");
        (await db.ProviderAccounts.SingleAsync()).Code.Should().Be("gemini-acc2");
    }

    [Fact]
    public async Task CreateAsync_SkipsExistingCodesFromOtherTenants()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var (db, dbFactory) = CreateDb();
        var existing = AiProvider.Create("Other tenant", "gemini-acc1", 101);
        existing.AssignTenant(otherTenantId);
        db.AiProviders.Add(existing);
        await db.SaveChangesAsync();

        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        clients.Create("gemini-broker-a").Returns(Substitute.For<ICLIProxyManagementClient>());
        var service = CreateService(db, dbFactory, tenant, clients);

        var result = await service.CreateAsync("Antigravity Two");

        result.Code.Should().Be("gemini-acc2");
    }

    [Fact]
    public async Task CreateAsync_AllowsBrokerSlotReuseAfterAccountTombstone()
    {
        var tenantId = Guid.NewGuid();
        var (db, dbFactory) = CreateDb();
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        clients.Create("gemini-broker-a").Returns(Substitute.For<ICLIProxyManagementClient>());
        var service = CreateService(db, dbFactory, tenant, clients);

        var deleted = await service.CreateAsync("Deleted account");
        var deletedAccount = await db.ProviderAccounts.SingleAsync(x => x.Id == deleted.AccountId);
        deletedAccount.Tombstone("test");
        await db.SaveChangesAsync();

        var replacement = await service.CreateAsync("Replacement account");

        replacement.Slot.Should().Be("gemini-broker-a");
        replacement.Code.Should().Be("gemini-acc2");
        (await db.ProviderAccounts.CountAsync(x => x.BrokerInstanceId == "gemini-broker-a"))
            .Should().Be(2);
        (await db.ProviderAccounts.CountAsync(x => x.DeletedAt == null && x.BrokerInstanceId == "gemini-broker-a"))
            .Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_RejectsAnActiveAccountUsingTheSelectedBrokerSlot()
    {
        var tenantId = Guid.NewGuid();
        var (db, dbFactory) = CreateDb();
        var provider = AiProvider.Create("Existing account", "gemini-acc1", 101);
        provider.AssignTenant(tenantId);
        db.AiProviders.Add(provider);
        db.ProviderAccounts.Add(ProviderAccount.Create(
            tenantId,
            provider.Id,
            "gemini-acc1",
            "Existing account",
            brokerInstanceId: "gemini-broker-a"));
        await db.SaveChangesAsync();

        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        clients.Create("gemini-broker-a").Returns(Substitute.For<ICLIProxyManagementClient>());
        var service = CreateService(db, dbFactory, tenant, clients);

        var act = () => service.CreateAsync("Replacement account");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("already assigned to an active Antigravity account");
    }

    [Fact]
    public async Task CreateAsync_CanonicalizesConfiguredSlotCasing()
    {
        var tenantId = Guid.NewGuid();
        var (db, dbFactory) = CreateDb();
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);
        var client = Substitute.For<ICLIProxyManagementClient>();
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        clients.Create("gemini-broker-a").Returns(client);
        var service = CreateService(db, dbFactory, tenant, clients);

        var result = await service.CreateAsync("Canonical account", "GEMINI-BROKER-A");

        result.Slot.Should().Be("gemini-broker-a");
        clients.Received(1).Create("gemini-broker-a");
        (await db.ProviderAccounts.SingleAsync()).BrokerInstanceId.Should().Be("gemini-broker-a");
    }

    [Fact]
    public async Task CreateAsync_RejectsMixedCaseActiveAccountUsingSlotInAnotherTenant()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var (db, dbFactory) = CreateDb();
        var provider = AiProvider.Create("Other tenant account", "gemini-acc1", 101);
        provider.AssignTenant(otherTenantId);
        db.AiProviders.Add(provider);
        db.ProviderAccounts.Add(ProviderAccount.Create(
            otherTenantId,
            provider.Id,
            "gemini-acc1",
            "Other tenant account",
            brokerInstanceId: "GEMINI-BROKER-A"));
        await db.SaveChangesAsync();

        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        clients.Create("gemini-broker-a").Returns(Substitute.For<ICLIProxyManagementClient>());
        var service = CreateService(db, dbFactory, tenant, clients);

        var act = () => service.CreateAsync("Replacement account", "gemini-broker-a");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("already assigned to an active Antigravity account");
    }

    [Fact]
    public void BrokerSlotIndex_IsCaseInsensitiveAndExcludesTombstonedAccounts()
    {
        using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var index = db.Model.FindEntityType(typeof(ProviderAccount))!
            .GetIndexes()
            .Single(x => x.Properties.Select(p => p.Name).SequenceEqual(["BrokerInstanceIdNormalized"]));

        index.IsUnique.Should().BeTrue();
        index.GetFilter().Should().Be("\"BrokerInstanceId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
    }

    [Fact]
    public async Task ListAsync_CanRunConcurrentlyWithoutSharingAContext()
    {
        var (db, dbFactory) = CreateDb();
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(Guid.NewGuid());
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        var service = CreateService(db, dbFactory, tenant, clients);

        var results = await Task.WhenAll(service.ListAsync(), service.ListAsync());

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(accounts => accounts.Should().BeEmpty());
    }

    [Fact]
    public async Task CreateAsync_RejectsAnUnconfiguredBrokerSlot()
    {
        var (db, dbFactory) = CreateDb();
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(Guid.NewGuid());
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        clients.Create(Arg.Any<string>()).Returns(_ => throw new ArgumentException("Broker slot is not allowlisted."));
        var service = CreateService(db, dbFactory, tenant, clients);

        var act = () => service.CreateAsync("Antigravity One", "missing-slot");

        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().Contain("allowlisted");
    }

    private static AntigravityAccountService CreateService(
        GatewayDbContext db,
        IDbContextFactory<GatewayDbContext> dbFactory,
        ITenantProvider tenant,
        ICLIProxyManagementClientFactory clients)
    {
        var options = Options.Create(new CLIProxyManagementOptions
        {
            DefaultSlot = "gemini-broker-a",
            Slots = new Dictionary<string, CLIProxySlotOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["gemini-broker-a"] = new() { BaseUrl = "http://gemini-broker-a:8317", ManagementKey = "test" }
            }
        });
        return new AntigravityAccountService(
            dbFactory,
            tenant,
            clients,
            options,
            NullLogger<AntigravityAccountService>.Instance);
    }

    private static (GatewayDbContext Context, IDbContextFactory<GatewayDbContext> Factory) CreateDb()
    {
        var name = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        var db = new GatewayDbContext(options);
        var factory = Substitute.For<IDbContextFactory<GatewayDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<GatewayDbContext>(new GatewayDbContext(
                new DbContextOptionsBuilder<GatewayDbContext>()
                    .UseInMemoryDatabase(name)
                    .Options)));
        return (db, factory);
    }
}
