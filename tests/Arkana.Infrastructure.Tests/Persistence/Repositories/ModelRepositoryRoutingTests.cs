using Arkana.Domain.Entities;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Tests.Persistence.Repositories;

public sealed class ModelRepositoryRoutingTests
{
    [Fact]
    public async Task GetByCodeAsync_PrefersEnabledModelAndStableProviderOrder()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new GatewayDbContext(options);
        var opencode = AiProvider.Create("OpenCode", "opencode", 0);
        var acc4 = AiProvider.Create("User Codex", "chatgpt-acc4", 0);
        db.AiProviders.AddRange(opencode, acc4);
        await db.SaveChangesAsync();

        var disabled = Model.Create(opencode.Id, "Luna disabled", "gpt-5.6-luna");
        disabled.Disable();
        db.Models.Add(disabled);
        db.Models.Add(Model.Create(acc4.Id, "Luna account", "gpt-5.6-luna"));
        await db.SaveChangesAsync();

        var result = await new ModelRepository(db).GetByCodeAsync("gpt-5.6-luna", CancellationToken.None);

        result.Should().NotBeNull();
        result!.IsEnabled.Should().BeTrue();
        result.Provider.Code.Should().Be("chatgpt-acc4");
    }
}
