using System.Data;
using Arkana.Domain.Entities;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Arkana.Infrastructure.Tests.Persistence.Repositories;

public sealed class ProviderAccountRepositoryTests
{
    private static GatewayDbContext Db() => new(new DbContextOptionsBuilder<GatewayDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>
    /// Relational context (SQLite in-memory) for optimistic-reservation tests:
    /// ExecuteUpdate is unsupported by the InMemory provider. The schema is
    /// generated for only the tables under test because the model's seed rows
    /// (HasData with concurrency tokens) cannot be applied on SQLite.
    /// </summary>
    private static (GatewayDbContext Context, SqliteConnection Connection) RelationalDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(connection)
            .Options;
        var ctx = new GatewayDbContext(options);
        var model = ctx.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        var operations = ctx.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel())
            .Where(op => op is CreateTableOperation table &&
                         table.Name is "ProviderAccounts" or "AiProviders" or "Tenants")
            .ToList();
        foreach (var command in ctx.GetService<IMigrationsSqlGenerator>().Generate(operations))
            ctx.Database.ExecuteSqlRaw(command.CommandText);
        return (ctx, connection);
    }

    [Fact]
    public async Task QueriesAreScopedToTenantAndProvider()
    {
        await using var db = Db();
        var repo = new ProviderAccountRepository(db);
        var provider = AiProvider.Create("Gemini", "gemini-test", 1);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var accountA = ProviderAccount.Create(tenantA, provider.Id, "Same-Code", "A");
        var accountB = ProviderAccount.Create(tenantB, provider.Id, "Same-Code", "B");
        accountA.MarkConnected(); accountB.MarkConnected();
        await repo.AddAsync(accountA); await repo.AddAsync(accountB);

        (await repo.GetByCodeAsync(tenantA, "same-code"))!.DisplayName.Should().Be("A");
        (await repo.GetByCodeAsync(tenantB, "same-code"))!.DisplayName.Should().Be("B");
        (await repo.GetHealthyAsync(tenantA, provider.Id)).Should().ContainSingle().Which.Id.Should().Be(accountA.Id);
    }

    [Fact]
    public async Task HealthyQueryExcludesDisabledAndCooledAccounts()
    {
        await using var db = Db();
        var repo = new ProviderAccountRepository(db);
        var provider = AiProvider.Create("Gemini", "gemini-test", 1);
        db.AiProviders.Add(provider); await db.SaveChangesAsync();
        var tenant = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var healthy = ProviderAccount.Create(tenant, provider.Id, "healthy", "Healthy"); healthy.MarkConnected();
        var cooled = ProviderAccount.Create(tenant, provider.Id, "cooled", "Cooled"); cooled.MarkConnected(); cooled.SetCooldown(now.AddHours(1));
        var disabled = ProviderAccount.Create(tenant, provider.Id, "disabled", "Disabled"); disabled.MarkConnected(); disabled.Disable();
        await repo.AddAsync(healthy); await repo.AddAsync(cooled); await repo.AddAsync(disabled);

        (await repo.GetHealthyAsync(tenant, provider.Id, now)).Should().ContainSingle().Which.Code.Should().Be("healthy");
    }

    [Fact]
    public async Task TombstonesAreHiddenByDefaultButAvailableForAudit()
    {
        await using var db = Db(); var repo = new ProviderAccountRepository(db);
        var provider = AiProvider.Create("Gemini", "gemini-test", 1); db.AiProviders.Add(provider); await db.SaveChangesAsync();
        var account = ProviderAccount.Create(Guid.NewGuid(), provider.Id, "deleted", "Deleted"); account.Tombstone("test"); await repo.AddAsync(account);
        (await repo.GetForProviderAsync(account.TenantId, provider.Id)).Should().BeEmpty();
        (await repo.GetForProviderAsync(account.TenantId, provider.Id, true)).Should().ContainSingle();
    }

    [Fact]
    public async Task TryReserveVersion_synchronizes_tracked_entity_for_follow_up_reads()
    {
        var (ctx, connection) = RelationalDb();
        await using var _ = ctx;
        using var __ = connection;
        var tenant = Guid.NewGuid();
        var account = ProviderAccount.Create(tenant, Guid.NewGuid(), "gemini-acc9", "acc9",
            ProviderAccountAuthOwnership.BrokerManagedOAuth, BrokerKind.CLIProxyAPI, brokerInstanceId: "gemini-broker-a");
        ctx.ProviderAccounts.Add(account);
        await ctx.SaveChangesAsync();
        var repo = new ProviderAccountRepository(ctx);

        var tracked = await repo.GetByIdAsync(tenant, account.Id);
        tracked.Should().NotBeNull();

        var reservation = Guid.NewGuid();
        (await repo.TryReserveVersionAsync(tenant, account.Id, tracked!.Version, reservation))
            .Should().BeTrue();

        var current = await repo.GetByIdAsync(tenant, account.Id);
        current!.Version.Should().Be(reservation,
            "a follow-up read must observe the reserved version, not the stale tracked value");
    }

    [Fact]
    public async Task TryReserveVersion_rejects_stale_expected_version()
    {
        var (ctx, connection) = RelationalDb();
        await using var _ = ctx;
        using var __ = connection;
        var tenant = Guid.NewGuid();
        var account = ProviderAccount.Create(tenant, Guid.NewGuid(), "gemini-acc8", "acc8",
            ProviderAccountAuthOwnership.BrokerManagedOAuth, BrokerKind.CLIProxyAPI, brokerInstanceId: "gemini-broker-a");
        ctx.ProviderAccounts.Add(account);
        await ctx.SaveChangesAsync();
        var repo = new ProviderAccountRepository(ctx);

        (await repo.TryReserveVersionAsync(tenant, account.Id, Guid.NewGuid(), Guid.NewGuid()))
            .Should().BeFalse();
    }

    [Fact]
    public async Task GetByIdNoTracking_reads_committed_version_after_external_update()
    {
        // Version fencing must never depend on the EF change tracker: a snapshot taken
        // earlier in the same (possibly long-lived) context stays stale after an
        // out-of-band version change, while the no-tracking read observes the committed
        // value. This is the read the chat reservation path relies on.
        var (ctx, connection) = RelationalDb();
        await using var _ = ctx;
        using var __ = connection;
        var tenant = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var account = ProviderAccount.Create(tenant, providerId, "gemini-acc7", "acc7",
            ProviderAccountAuthOwnership.BrokerManagedOAuth, BrokerKind.CLIProxyAPI, brokerInstanceId: "gemini-broker-a");
        account.MarkConnected();
        ctx.ProviderAccounts.Add(account);
        await ctx.SaveChangesAsync();
        var repo = new ProviderAccountRepository(ctx);

        await repo.GetForProviderAsync(tenant, providerId, false); // seeds the tracker
        var external = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options;
        await using (var otherRequest = new GatewayDbContext(options))
        {
            (await otherRequest.ProviderAccounts
                .Where(x => x.Id == account.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, external)))
                .Should().Be(1);
        }

        var tracked = await repo.GetByIdAsync(tenant, account.Id);
        var fresh = await repo.GetByIdNoTrackingAsync(tenant, account.Id);

        tracked!.Version.Should().NotBe(external, "the tracked instance keeps its stale snapshot");
        fresh!.Version.Should().Be(external, "the no-tracking read observes the committed version");
        (await repo.TryReserveVersionAsync(tenant, account.Id, fresh.Version, Guid.NewGuid())).Should().BeTrue();
        (await repo.TryReserveVersionAsync(tenant, account.Id, tracked.Version, Guid.NewGuid())).Should().BeFalse();
    }
}
