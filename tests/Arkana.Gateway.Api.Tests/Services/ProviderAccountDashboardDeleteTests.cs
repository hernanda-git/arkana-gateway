using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Gateway.Api.Services;
using Arkana.Infrastructure.Broker;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Entities;
using Arkana.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Arkana.Gateway.Api.Tests.Services;

public sealed class ProviderAccountDashboardDeleteTests
{
    [Fact]
    public async Task DashboardDeleteProvider_UsesBrokerAccountLifecycle()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        var provider = await db.AiProviders.SingleAsync();
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetByIdAsync(provider.Id, tenantId, Arg.Any<CancellationToken>()).Returns(provider);
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        clients.Create("gemini-broker-a").Returns(client);
        var facade = new ProviderAccountDashboardFacade(accounts, clients, db, tenant);
        var service = CreateDashboardService(providerRepo, accounts, tenant, facade);

        await service.DeleteProviderAsync(provider.Id);

        account.DeletedAt.Should().NotBeNull();
        await providerRepo.DidNotReceive().DeleteAsync(provider.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DashboardDeleteProvider_DoesNotDeleteProviderFromAnotherTenant()
    {
        var ownerTenantId = Guid.NewGuid();
        var foreignTenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(ownerTenantId);
        var provider = await db.AiProviders.SingleAsync();
        var providerRepo = Substitute.For<IAiProviderRepository>();
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(foreignTenantId);
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        clients.Create("gemini-broker-a").Returns(client);
        var facade = new ProviderAccountDashboardFacade(accounts, clients, db, tenant);
        var service = CreateDashboardService(providerRepo, accounts, tenant, facade);

        await service.DeleteProviderAsync(provider.Id);

        await providerRepo.DidNotReceive().GetByIdAsync(provider.Id, Arg.Any<CancellationToken>());
        await providerRepo.DidNotReceive().DeleteAsync(provider.Id, Arg.Any<CancellationToken>());
        await providerRepo.DidNotReceive().DeleteAsync(provider.Id, foreignTenantId, Arg.Any<CancellationToken>());
        account.DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task DeleteForProviderAsync_TombstonesPendingAccountWithoutBrokerCredential()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        var facade = CreateFacade(db, tenantId, accounts, client);

        var result = await facade.DeleteForProviderAsync(
            account.AiProviderId,
            "dashboard",
            "delete-provider",
            CancellationToken.None);

        result.Should().BeTrue();
        account.DeletedAt.Should().NotBeNull();
        account.IsEnabled.Should().BeFalse();
        account.ConnectionStatus.Should().Be(ProviderAccountStatus.Disabled);
        await client.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        var operation = await db.ProviderAccountOperations.SingleAsync();
        operation.Kind.Should().Be(ProviderAccountOperationKind.Delete);
        operation.State.Should().Be(ProviderAccountOperationState.Succeeded);
    }

    [Fact]
    public async Task DeleteForProviderAsync_TombstonesEveryActiveAccountForProvider()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        var second = ProviderAccount.Create(
            tenantId,
            account.AiProviderId,
            "gemini-acc1-secondary",
            "Antigravity Secondary",
            brokerInstanceId: "gemini-broker-a");
        second.BindBrokerCredential("stable-auth-2");
        second.MarkConnected();
        db.ProviderAccounts.Add(second);
        await db.SaveChangesAsync();

        accounts.GetByIdAsync(tenantId, second.Id, Arg.Any<CancellationToken>()).Returns(second);
        accounts.TryReserveVersionAsync(
                tenantId,
                second.Id,
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                SetVersion(second, callInfo.ArgAt<Guid>(3));
                return true;
            });
        var facade = CreateFacade(db, tenantId, accounts, client);

        var result = await facade.DeleteForProviderAsync(
            account.AiProviderId,
            "dashboard",
            "delete-provider",
            CancellationToken.None);

        result.Should().BeTrue();
        account.DeletedAt.Should().NotBeNull();
        second.DeletedAt.Should().NotBeNull();
        await client.Received(1).DeleteAsync("stable-auth-2", Arg.Any<CancellationToken>());
        (await db.ProviderAccountOperations.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task DeleteAsync_UsesStoredCredentialForConnectedAccount()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        account.BindBrokerCredential("stable-auth-1");
        account.MarkConnected();
        var expectedVersion = account.Version;
        var pending = Substitute.For<IOAuthPendingFlowRepository>();
        var facade = CreateFacade(db, tenantId, accounts, client, pending: pending);

        var result = await facade.DeleteAsync(
            account.Id,
            "gemini-broker-a",
            auth: null,
            "dashboard",
            "delete-connected",
            expectedVersion,
            CancellationToken.None);

        result.Should().BeTrue();
        account.DeletedAt.Should().NotBeNull();
        await client.Received(1).DeleteAsync("stable-auth-1", Arg.Any<CancellationToken>());
        await pending.Received(1).DeletePendingForAccountAsync(
            account.Code, tenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDeletedProviderIdsAsync_DoesNotHideProviderWhileAnAccountRemainsActive()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        var second = ProviderAccount.Create(
            tenantId,
            account.AiProviderId,
            "gemini-acc1-secondary",
            "Antigravity Secondary",
            brokerInstanceId: "gemini-broker-a");
        db.ProviderAccounts.Add(second);
        account.Tombstone("partial-delete");
        await db.SaveChangesAsync();
        var facade = CreateFacade(db, tenantId, accounts, client);

        (await facade.GetDeletedProviderIdsAsync()).Should().NotContain(account.AiProviderId);
    }

    [Fact]
    public async Task GetDeletedProviderIdsAsync_HidesProviderAfterEveryAccountIsTombstoned()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        var second = ProviderAccount.Create(
            tenantId,
            account.AiProviderId,
            "gemini-acc1-secondary",
            "Antigravity Secondary",
            brokerInstanceId: "gemini-broker-a");
        db.ProviderAccounts.Add(second);
        account.Tombstone("delete-one");
        second.Tombstone("delete-two");
        await db.SaveChangesAsync();
        var facade = CreateFacade(db, tenantId, accounts, client);

        (await facade.GetDeletedProviderIdsAsync()).Should().Contain(account.AiProviderId);
    }

    [Fact]
    public async Task DeleteForProviderAsync_ReturnsTrueForAlreadyTombstonedAccount()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        account.Tombstone("previous-delete");
        await db.SaveChangesAsync();
        var pending = Substitute.For<IOAuthPendingFlowRepository>();
        var facade = CreateFacade(db, tenantId, accounts, client, pending: pending);

        var result = await facade.DeleteForProviderAsync(
            account.AiProviderId,
            "dashboard",
            "delete-again",
            CancellationToken.None);

        result.Should().BeTrue();
        await accounts.DidNotReceive().AcquireMutationLeaseAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await pending.Received(1).DeletePendingForAccountAsync(
            account.Code, tenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DashboardProviderList_WithoutTenantFailsClosed()
    {
        var (db, account, accounts, client) = CreateFixture(Guid.NewGuid());
        var provider = await db.AiProviders.SingleAsync();
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync().Returns(new[] { provider });
        var tenant = Substitute.For<ITenantProvider>();
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        clients.Create("gemini-broker-a").Returns(client);
        var facade = new ProviderAccountDashboardFacade(accounts, clients, db, tenant);
        var service = CreateDashboardService(providerRepo, accounts, tenant, facade);

        var result = await service.GetProvidersAsync();

        result.Should().BeEmpty();
        await providerRepo.DidNotReceive().GetAllAsync();
    }

    [Fact]
    public async Task ToggleProvider_WithoutTenantFailsClosed()
    {
        var tenant = Substitute.For<ITenantProvider>();
        var providerRepo = Substitute.For<IAiProviderRepository>();
        var accounts = Substitute.For<IProviderAccountRepository>();
        var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        var facade = new ProviderAccountDashboardFacade(accounts, clients, db, tenant);
        var service = CreateDashboardService(providerRepo, accounts, tenant, facade);

        var act = () => service.ToggleProviderAsync(Guid.NewGuid());

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Authenticated tenant");
        await providerRepo.DidNotReceive().GetByIdAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToggleProvider_DoesNotUpdateAnotherTenantProvider()
    {
        var ownerTenantId = Guid.NewGuid();
        var foreignTenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(ownerTenantId);
        var provider = await db.AiProviders.SingleAsync();
        var providerRepo = Substitute.For<IAiProviderRepository>();
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(foreignTenantId);
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        clients.Create("gemini-broker-a").Returns(client);
        var facade = new ProviderAccountDashboardFacade(accounts, clients, db, tenant);
        var service = CreateDashboardService(providerRepo, accounts, tenant, facade);

        await service.ToggleProviderAsync(provider.Id);

        await providerRepo.DidNotReceive().GetByIdAsync(provider.Id, Arg.Any<CancellationToken>());
        await providerRepo.DidNotReceive().UpdateAsync(
            Arg.Any<AiProvider>(), Arg.Any<CancellationToken>());
        account.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task RecoverDeleteAsync_WhenCredentialIsAbsent_FinalizesTombstoneWithoutBrokerMutation()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        var operation = CreateDeleteOperation(account);
        db.ProviderAccountOperations.Add(operation);
        await db.SaveChangesAsync();
        var facade = CreateFacade(db, tenantId, accounts, client);

        var result = await facade.RecoverDeleteAsync(
            account.Id,
            operation.Id,
            "dashboard",
            "recover-delete",
            CancellationToken.None);

        result.State.Should().Be(ProviderAccountOperationState.Succeeded.ToString());
        result.BrokerCredentialFound.Should().BeFalse();
        result.BrokerDeletePerformed.Should().BeFalse();
        account.DeletedAt.Should().NotBeNull();
        (await db.ProviderAccountOperations.SingleAsync()).State
            .Should().Be(ProviderAccountOperationState.Succeeded);
        await client.DidNotReceive().ListAuthFilesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoverDeleteAsync_WhenCredentialIsStillPresent_DeletesAfterBrokerRead()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        account.BindBrokerCredential("stable-auth-1");
        account.MarkConnected();
        var operation = CreateDeleteOperation(account);
        db.ProviderAccounts.Update(account);
        db.ProviderAccountOperations.Add(operation);
        await db.SaveChangesAsync();
        client.ListAuthFilesAsync(Arg.Any<CancellationToken>()).Returns(
            new CLIProxyAuthFiles(
                "gemini-broker-a",
                new[] { new CLIProxyAuthFile("stable-auth-1", false, "antigravity", "Antigravity One") }));
        client.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var facade = CreateFacade(db, tenantId, accounts, client);

        var result = await facade.RecoverDeleteAsync(
            account.Id,
            operation.Id,
            "dashboard",
            "recover-delete-connected",
            CancellationToken.None);

        result.BrokerCredentialFound.Should().BeTrue();
        result.BrokerDeletePerformed.Should().BeTrue();
        account.DeletedAt.Should().NotBeNull();
        await client.Received(1).DeleteAsync("stable-auth-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoverDeleteAsync_WhenBrokerReadFails_DisablesAccountAndKeepsOperationIndeterminate()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        account.BindBrokerCredential("stable-auth-1");
        account.MarkConnected();
        var operation = CreateDeleteOperation(account);
        db.ProviderAccounts.Update(account);
        db.ProviderAccountOperations.Add(operation);
        await db.SaveChangesAsync();
        client.ListAuthFilesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<CLIProxyAuthFiles>(new HttpRequestException("broker unavailable")));
        var facade = CreateFacade(db, tenantId, accounts, client);

        var act = () => facade.RecoverDeleteAsync(
            account.Id,
            operation.Id,
            "dashboard",
            "recover-delete-failed",
            CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        account.ConnectionStatus.Should().Be(ProviderAccountStatus.OutOfSync);
        account.IsEnabled.Should().BeFalse();
        (await db.ProviderAccountOperations.SingleAsync()).State
            .Should().Be(ProviderAccountOperationState.Indeterminate);
        await client.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteForProviderAsync_TombstonesGatewayManagedPendingAccount()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId, gatewayManaged: true);
        var oauth = Substitute.For<IOAuthFlowService>();
        var pending = Substitute.For<IOAuthPendingFlowRepository>();
        oauth.DisconnectAsync(account.Code, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        pending.DeletePendingForAccountAsync(account.Code, tenantId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var facade = CreateFacade(db, tenantId, accounts, client, oauth, pending);

        var result = await facade.DeleteForProviderAsync(
            account.AiProviderId,
            "dashboard",
            "delete-gateway-pending",
            CancellationToken.None);

        result.Should().BeTrue();
        account.DeletedAt.Should().NotBeNull();
        await oauth.Received(1).DisconnectAsync(account.Code, Arg.Any<CancellationToken>());
        await pending.Received(1).DeletePendingForAccountAsync(account.Code, tenantId, Arg.Any<CancellationToken>());
        await client.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteForProviderAsync_TombstonesGatewayManagedConnectedAccount()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId, gatewayManaged: true);
        account.MarkConnected();
        await db.SaveChangesAsync();
        var oauth = Substitute.For<IOAuthFlowService>();
        var pending = Substitute.For<IOAuthPendingFlowRepository>();
        oauth.DisconnectAsync(account.Code, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        pending.DeletePendingForAccountAsync(account.Code, tenantId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var facade = CreateFacade(db, tenantId, accounts, client, oauth, pending);

        var result = await facade.DeleteForProviderAsync(
            account.AiProviderId,
            "dashboard",
            "delete-gateway-connected",
            CancellationToken.None);

        result.Should().BeTrue();
        account.DeletedAt.Should().NotBeNull();
        account.IsEnabled.Should().BeFalse();
        await oauth.Received(1).DisconnectAsync(account.Code, Arg.Any<CancellationToken>());
        await pending.Received(1).DeletePendingForAccountAsync(account.Code, tenantId, Arg.Any<CancellationToken>());
        await client.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOAuthAccountsAsync_HidesTombstonedGatewayAccountAfterDelete()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId, gatewayManaged: true);
        var oauth = Substitute.For<IOAuthFlowService>();
        var pending = Substitute.For<IOAuthPendingFlowRepository>();
        oauth.DisconnectAsync(account.Code, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        pending.DeletePendingForAccountAsync(account.Code, tenantId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var facade = CreateFacade(db, tenantId, accounts, client, oauth, pending);
        (await facade.DeleteForProviderAsync(account.AiProviderId, "dashboard", "delete-reload"))
            .Should().BeTrue();
        var storedVersion = await db.ProviderAccounts
            .AsNoTracking()
            .Where(x => x.Id == account.Id)
            .Select(x => x.Version)
            .SingleAsync();
        db.ProviderAccounts.Attach(account);
        db.Entry(account).Property(x => x.Version).OriginalValue = storedVersion;
        db.Entry(account).State = EntityState.Modified;
        await db.SaveChangesAsync();
        var provider = await db.AiProviders.SingleAsync();
        provider.SetAuthMethod(AuthMethod.OAuth, Guid.NewGuid());
        await db.SaveChangesAsync();
        var providerRepo = Substitute.For<IAiProviderRepository>();
        providerRepo.GetAllAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns(new[] { provider });
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);
        var service = CreateDashboardService(providerRepo, accounts, tenant, facade);

        var result = await service.GetOAuthAccountsAsync(provider.OAuthConfigId!.Value);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task RecoverDeleteAsync_TombstonesGatewayManagedAccountAfterIndeterminateDelete()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId, gatewayManaged: true);
        var operation = CreateDeleteOperation(account);
        db.ProviderAccountOperations.Add(operation);
        await db.SaveChangesAsync();
        var oauth = Substitute.For<IOAuthFlowService>();
        var pending = Substitute.For<IOAuthPendingFlowRepository>();
        oauth.DisconnectAsync(account.Code, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        pending.DeletePendingForAccountAsync(account.Code, tenantId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var facade = CreateFacade(db, tenantId, accounts, client, oauth, pending);

        var result = await facade.RecoverDeleteAsync(
            account.Id,
            operation.Id,
            "dashboard",
            "recover-gateway-delete",
            CancellationToken.None);

        result.State.Should().Be(ProviderAccountOperationState.Succeeded.ToString());
        account.DeletedAt.Should().NotBeNull();
        await oauth.Received(1).DisconnectAsync(account.Code, Arg.Any<CancellationToken>());
        await pending.Received(1).DeletePendingForAccountAsync(account.Code, tenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePendingForAccountAsync_RemovesNonChatGptPendingFlowsForAccount()
    {
        using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var tenant = Tenant.Create("Owner", "owner", "{}");
        var foreignTenant = Tenant.Create("Foreign", "foreign", "{}");
        db.Tenants.AddRange(tenant, foreignTenant);
        await db.SaveChangesAsync();
        var vault = Substitute.For<ICredentialVault>();
        vault.Seal(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var owned = OAuthPendingFlow.Create(
            tenant.Id,
            "gemini",
            "owned-state",
            "verifier",
            "https://gateway.test/oauth/gemini/callback",
            vault,
            TimeSpan.FromMinutes(10),
            aiProviderCode: "gemini-acc1");
        var foreign = OAuthPendingFlow.Create(
            foreignTenant.Id,
            "gemini",
            "foreign-state",
            "verifier",
            "https://gateway.test/oauth/gemini/callback",
            vault,
            TimeSpan.FromMinutes(10),
            aiProviderCode: "gemini-acc1");
        db.OAuthPendingFlows.AddRange(owned, foreign);
        await db.SaveChangesAsync();

        await new OAuthPendingFlowRepository(db)
            .DeletePendingForAccountAsync("gemini-acc1", tenant.Id);

        (await db.OAuthPendingFlows.AsNoTracking().ToListAsync())
            .Select(flow => flow.State)
            .Should().BeEquivalentTo("foreign-state");
    }

    [Fact]
    public async Task StartAsync_PersistsAuthorizationUrlForBrokerStateRouting()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        var url = "https://staging.arkana.dev/oauth/gemini/callback?state=state-start-123";
        client.StartOAuthAsync(Arg.Any<CancellationToken>())
            .Returns(new CLIProxyOAuthStart("gemini-broker-a", url, DateTimeOffset.UtcNow.AddMinutes(10)));
        var facade = CreateFacade(db, tenantId, accounts, client);

        var result = await facade.StartAsync(
            account.Id,
            "gemini-broker-a",
            reconnect: true,
            "start-state-routing",
            account.Version,
            CancellationToken.None);

        result.AuthorizationUrl.Should().Be(url);
        (await db.ProviderAccountOperations.SingleAsync()).AuthorizationUrl.Should().Be(url);
    }

    [Fact]
    public async Task TryHandleBrokerCallbackAsync_RoutesMatchingStateToBoundBrokerSlot()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        const string state = "state-route-123";
        db.ProviderAccountOperations.Add(new ProviderAccountOperation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProviderAccountId = account.Id,
            Kind = ProviderAccountOperationKind.Reconnect,
            State = ProviderAccountOperationState.Succeeded,
            AuthorizationUrl = $"https://staging.arkana.dev/oauth/gemini/callback?state={state}",
            Slot = "gemini-broker-a",
            Nonce = Guid.NewGuid().ToString("N"),
            IdempotencyKey = "route-state",
            RequestFingerprint = "route-state",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        await db.SaveChangesAsync();
        client.SubmitOAuthCallbackAsync(state, "code-redacted", null, Arg.Any<CancellationToken>())
            .Returns(new CLIProxyOAuthCallbackResult(CLIProxyOAuthCallbackDisposition.Accepted));
        var facade = CreateFacade(db, tenantId, accounts, client);

        var result = await facade.TryHandleBrokerCallbackAsync(
            "gemini",
            "code-redacted",
            state,
            null,
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Handled.Should().BeTrue();
        result.Disposition.Should().Be(CLIProxyOAuthCallbackDisposition.Accepted);
        await client.Received(1).SubmitOAuthCallbackAsync(
            state,
            "code-redacted",
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryHandleBrokerCallbackAsync_IgnoresUnknownState()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        db.ProviderAccountOperations.Add(new ProviderAccountOperation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProviderAccountId = account.Id,
            Kind = ProviderAccountOperationKind.Start,
            State = ProviderAccountOperationState.Succeeded,
            AuthorizationUrl = "https://staging.arkana.dev/oauth/gemini/callback?state=known-state",
            Slot = "gemini-broker-a",
            Nonce = Guid.NewGuid().ToString("N"),
            IdempotencyKey = "unknown-state",
            RequestFingerprint = "unknown-state",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        await db.SaveChangesAsync();
        var facade = CreateFacade(db, tenantId, accounts, client);

        var result = await facade.TryHandleBrokerCallbackAsync(
            "gemini",
            "code-redacted",
            "unknown-state",
            null,
            CancellationToken.None);

        result.Should().BeNull();
        await client.DidNotReceive().SubmitOAuthCallbackAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryHandleBrokerCallbackAsync_FailsClosedWhenStateMapsToMultipleTenants()
    {
        var tenantId = Guid.NewGuid();
        var foreignTenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        var foreignProvider = AiProvider.Create("Foreign Antigravity", "foreign-acc", 202);
        foreignProvider.AssignTenant(foreignTenantId);
        var foreignAccount = ProviderAccount.Create(
            foreignTenantId,
            foreignProvider.Id,
            "foreign-acc",
            "Foreign Antigravity",
            brokerInstanceId: "gemini-broker-a");
        db.AiProviders.Add(foreignProvider);
        db.ProviderAccounts.Add(foreignAccount);
        const string state = "state-duplicate-123";
        db.ProviderAccountOperations.AddRange(
            new ProviderAccountOperation
            {
                Id = Guid.NewGuid(), TenantId = tenantId, ProviderAccountId = account.Id,
                Kind = ProviderAccountOperationKind.Start, State = ProviderAccountOperationState.Succeeded,
                AuthorizationUrl = $"https://staging.arkana.dev/oauth/gemini/callback?state={state}",
                Slot = "gemini-broker-a", Nonce = Guid.NewGuid().ToString("N"), IdempotencyKey = "duplicate-a",
                RequestFingerprint = "duplicate-a", CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            },
            new ProviderAccountOperation
            {
                Id = Guid.NewGuid(), TenantId = foreignTenantId, ProviderAccountId = foreignAccount.Id,
                Kind = ProviderAccountOperationKind.Start, State = ProviderAccountOperationState.Succeeded,
                AuthorizationUrl = $"https://staging.arkana.dev/oauth/gemini/callback?state={state}",
                Slot = "gemini-broker-a", Nonce = Guid.NewGuid().ToString("N"), IdempotencyKey = "duplicate-b",
                RequestFingerprint = "duplicate-b", CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            });
        await db.SaveChangesAsync();
        var facade = CreateFacade(db, tenantId, accounts, client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.TryHandleBrokerCallbackAsync(
            "gemini",
            "code-redacted",
            state,
            null,
            CancellationToken.None));
        await client.DidNotReceive().SubmitOAuthCallbackAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryHandleBrokerCallbackAsync_RejectsOversizedCodeBeforeBrokerCall()
    {
        var tenantId = Guid.NewGuid();
        var (db, account, accounts, client) = CreateFixture(tenantId);
        const string state = "state-size-123";
        db.ProviderAccountOperations.Add(new ProviderAccountOperation
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ProviderAccountId = account.Id,
            Kind = ProviderAccountOperationKind.Start, State = ProviderAccountOperationState.Succeeded,
            AuthorizationUrl = $"https://staging.arkana.dev/oauth/gemini/callback?state={state}",
            Slot = "gemini-broker-a", Nonce = Guid.NewGuid().ToString("N"), IdempotencyKey = "size-state",
            RequestFingerprint = "size-state", CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        await db.SaveChangesAsync();
        var facade = CreateFacade(db, tenantId, accounts, client);

        await Assert.ThrowsAsync<ArgumentException>(() => facade.TryHandleBrokerCallbackAsync(
            "gemini",
            new string('x', 8193),
            state,
            null,
            CancellationToken.None));
        await client.DidNotReceive().SubmitOAuthCallbackAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    private static ProviderAccountOperation CreateDeleteOperation(ProviderAccount account)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = account.TenantId,
            ProviderAccountId = account.Id,
            Kind = ProviderAccountOperationKind.Delete,
            State = ProviderAccountOperationState.Indeterminate,
            Nonce = Guid.NewGuid().ToString("N"),
            IdempotencyKey = $"delete-{Guid.NewGuid():N}",
            RequestFingerprint = "test-fingerprint",
            ExpectedVersion = account.Version,
            Slot = account.BrokerInstanceId ?? account.Code,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(9)
        };

    private static ProviderAccountDashboardFacade CreateFacade(
        GatewayDbContext db,
        Guid tenantId,
        IProviderAccountRepository accounts,
        ICLIProxyManagementClient client,
        IOAuthFlowService? oauth = null,
        IOAuthPendingFlowRepository? pending = null)
    {
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);
        var clients = Substitute.For<ICLIProxyManagementClientFactory>();
        clients.Create("gemini-broker-a").Returns(client);
        return new ProviderAccountDashboardFacade(accounts, clients, db, tenant, oauth, pending);
    }

    private static (GatewayDbContext Db, ProviderAccount Account, IProviderAccountRepository Accounts, ICLIProxyManagementClient Client)
        CreateFixture(Guid tenantId, bool gatewayManaged = false)
    {
        var provider = AiProvider.Create("Antigravity One", "gemini-acc1", 101);
        provider.AssignTenant(tenantId);
        var account = gatewayManaged
            ? ProviderAccount.Create(
                tenantId,
                provider.Id,
                "gemini-acc1",
                "Antigravity One",
                ProviderAccountAuthOwnership.GatewayManagedOAuth,
                brokerKind: null)
            : ProviderAccount.Create(
                tenantId,
                provider.Id,
                "gemini-acc1",
                "Antigravity One",
                brokerInstanceId: "gemini-broker-a");

        var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.AiProviders.Add(provider);
        db.ProviderAccounts.Add(account);
        db.SaveChanges();

        var accounts = Substitute.For<IProviderAccountRepository>();
        accounts.GetByIdAsync(tenantId, account.Id, Arg.Any<CancellationToken>()).Returns(account);
        accounts.TryReserveVersionAsync(
                tenantId,
                account.Id,
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                SetVersion(account, callInfo.ArgAt<Guid>(3));
                return true;
            });
        accounts.AcquireMutationLeaseAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IAsyncDisposable>(new NoopLease()));

        var client = Substitute.For<ICLIProxyManagementClient>();
        return (db, account, accounts, client);
    }

    private static DashboardService CreateDashboardService(
        IAiProviderRepository providers,
        IProviderAccountRepository accounts,
        ITenantProvider tenant,
        ProviderAccountDashboardFacade facade)
        => new(
            providers,
            Substitute.For<IApiKeyRepository>(),
            Substitute.For<IModelRepository>(),
            Substitute.For<ITokenTracker>(),
            Substitute.For<IRequestLogger>(),
            new ActiveStreamCounter(),
            Substitute.For<IN8nService>(),
            new UserTimeService(Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>()),
            Substitute.For<ICredentialVault>(),
            new UrlSafetyValidator(
                new UrlSafetyOptions { AllowHttp = true, AllowPrivateAddresses = true },
                new DnsResolver()),
            Substitute.For<System.Net.Http.IHttpClientFactory>(),
            Substitute.For<IApiKeyPoolRepository>(),
            Substitute.For<IAgentRepository>(),
            new AgentOrchestrator(
                Substitute.For<IAgentRepository>(),
                Substitute.For<IModelRepository>(),
                Substitute.For<IAiProviderRepository>(),
                Substitute.For<Arkana.Domain.Interfaces.Canonical.IProviderConnectorFactory>(),
                Substitute.For<Microsoft.Extensions.Logging.ILogger<AgentOrchestrator>>()),
            Substitute.For<IOAuthFlowService>(),
            Substitute.For<IOAuthProviderConfigRepository>(),
            Substitute.For<Microsoft.AspNetCore.Components.NavigationManager>(),
            providerAccounts: accounts,
            tenantProvider: tenant,
            providerAccountDashboard: facade);

    private static void SetVersion(ProviderAccount account, Guid version)
        => typeof(ProviderAccount).GetProperty(nameof(ProviderAccount.Version))!.SetValue(account, version);

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
