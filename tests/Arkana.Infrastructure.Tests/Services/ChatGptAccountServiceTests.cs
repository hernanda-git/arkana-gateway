using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Infrastructure.OAuth;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Repositories;
using Arkana.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.Services;

public sealed class ChatGptAccountServiceTests
{
    [Fact]
    public async Task RemoveAsync_DeletesOwnedPendingFlowsButNotForeignTenantFlows()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options);
        var vault = Substitute.For<ICredentialVault>();
        vault.Seal(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var tenant = Tenant.Create("Owner", "owner", "{}");
        var foreignTenant = Tenant.Create("Foreign", "foreign", "{}");
        db.Tenants.AddRange(tenant, foreignTenant);
        var provider = AiProvider.Create("ChatGPT Account 1", "chatgpt-acc1", 100);
        provider.AssignTenant(tenant.Id);
        db.AiProviders.Add(provider);
        var owned = OAuthPendingFlow.Create(
            tenant.Id,
            "gemini",
            "owned-state",
            "verifier",
            "https://gateway.test/oauth/gemini/callback",
            vault,
            TimeSpan.FromMinutes(10),
            aiProviderCode: "chatgpt-acc1");
        var foreign = OAuthPendingFlow.Create(
            foreignTenant.Id,
            "gemini",
            "foreign-state",
            "verifier",
            "https://gateway.test/oauth/gemini/callback",
            vault,
            TimeSpan.FromMinutes(10),
            aiProviderCode: "chatgpt-acc1");
        db.OAuthPendingFlows.AddRange(owned, foreign);
        await db.SaveChangesAsync();

        var service = new ChatGptAccountService(
            db,
            new StaticTenantProvider(tenant.Id),
            NullLogger<ChatGptAccountService>.Instance,
            new OAuthPendingFlowRepository(db));

        Assert.True(await service.RemoveAsync("chatgpt-acc1"));

        Assert.Null(await db.AiProviders.SingleOrDefaultAsync(p => p.Id == provider.Id));
        (await db.OAuthPendingFlows.AsNoTracking().Select(f => f.State).ToListAsync())
            .Should().BeEquivalentTo("foreign-state");
    }

    private sealed class StaticTenantProvider(Guid tenantId) : ITenantProvider
    {
        public Guid? TenantId { get; } = tenantId;
    }
}
