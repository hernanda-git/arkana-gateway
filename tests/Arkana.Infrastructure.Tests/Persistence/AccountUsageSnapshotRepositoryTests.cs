using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.Persistence;

public sealed class AccountUsageSnapshotRepositoryTests
{
    private static GatewayDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new GatewayDbContext(options);
    }

    [Fact]
    public async Task GetByAccountCodesAsync_OnlyReturnsSnapshotsForRequestedTenant()
    {
        using var db = CreateDbContext();
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var currentProvider = AiProvider.Create("Current", "chatgpt-acc1", 0);
        currentProvider.AssignTenant(tenant);
        var otherProvider = AiProvider.Create("Other", "chatgpt-acc1", 0);
        otherProvider.AssignTenant(otherTenant);
        db.AiProviders.AddRange(currentProvider, otherProvider);
        db.AccountUsageSnapshots.AddRange(
            new AccountUsageSnapshot(currentProvider.Id, "chatgpt-acc1", UsageWindowKind.Primary,
                10, 300, null, "plus", tenant),
            new AccountUsageSnapshot(otherProvider.Id, "chatgpt-acc1", UsageWindowKind.Primary,
                90, 300, null, "plus", otherTenant));
        await db.SaveChangesAsync();

        var repo = new AccountUsageSnapshotRepository(db);
        var result = await repo.GetByAccountCodesAsync(tenant, ["chatgpt-acc1"]);

        result.Should().ContainSingle();
        result[0].TenantId.Should().Be(tenant);
        result[0].UsedPercent.Should().Be(10);
    }

    [Fact]
    public async Task UpsertAsync_RejectsSnapshotForAnotherTenant()
    {
        using var db = CreateDbContext();
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var provider = AiProvider.Create("Other", "chatgpt-acc1", 0);
        provider.AssignTenant(otherTenant);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync();
        var current = Substitute.For<ITenantProvider>();
        current.TenantId.Returns(tenant);
        var repo = new AccountUsageSnapshotRepository(db, current);
        var snapshot = new AccountUsageSnapshot(
            provider.Id, "chatgpt-acc1", UsageWindowKind.Primary, 90, 300, null, "plus");

        var written = await repo.UpsertAsync(snapshot);

        written.Should().BeFalse();
        (await db.AccountUsageSnapshots.CountAsync()).Should().Be(0);
    }
}
