using Arkana.Domain.Entities;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Tests.Persistence;

public sealed class PolicyTemplateRepositoryTests
{
    private static GatewayDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase($"repo_test_{Guid.NewGuid()}")
            .Options;
        return new GatewayDbContext(opts);
    }

    private static PolicyTemplate CreateTemplate(string name = "Test", string slug = "test")
        => PolicyTemplate.Create(name, slug, "Test description", """{"key": "value"}""");

    [Fact]
    public async Task AddAsync_InsertsTemplate()
    {
        using var ctx = CreateContext();
        var repo = new PolicyTemplateRepository(ctx);
        var template = CreateTemplate();

        await repo.AddAsync(template);

        var result = await ctx.PolicyTemplates.FindAsync(template.Id);
        result.Should().NotBeNull();
        result!.Name.Should().Be("Test");
    }

    [Fact]
    public async Task GetAllAsync_ReturnsOnlyActive()
    {
        using var ctx = CreateContext();
        var repo = new PolicyTemplateRepository(ctx);

        await repo.AddAsync(CreateTemplate("Active", "active"));
        var inactive = CreateTemplate("Inactive", "inactive");
        inactive.Deactivate();
        await repo.AddAsync(inactive);

        var all = await repo.GetAllAsync();
        all.Should().HaveCount(1);
        all[0].Slug.Should().Be("active");
    }

    [Fact]
    public async Task GetBySlugAsync_FindsTemplate()
    {
        using var ctx = CreateContext();
        var repo = new PolicyTemplateRepository(ctx);

        await repo.AddAsync(CreateTemplate("GDPR", "gdpr"));

        var result = await repo.GetBySlugAsync("gdpr");
        result.Should().NotBeNull();
        result!.Name.Should().Be("GDPR");
    }

    [Fact]
    public async Task GetBySlugAsync_CaseInsensitive()
    {
        using var ctx = CreateContext();
        var repo = new PolicyTemplateRepository(ctx);

        await repo.AddAsync(CreateTemplate("GDPR", "gdpr"));

        var result = await repo.GetBySlugAsync("GDPR");
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_FindsTemplate()
    {
        using var ctx = CreateContext();
        var repo = new PolicyTemplateRepository(ctx);

        var template = CreateTemplate();
        await repo.AddAsync(template);

        var result = await repo.GetByIdAsync(template.Id);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateAsync_ModifiesTemplate()
    {
        using var ctx = CreateContext();
        var repo = new PolicyTemplateRepository(ctx);

        var template = CreateTemplate();
        await repo.AddAsync(template);

        template.UpdateDetails("Updated", "new desc", """{"updated": true}""");
        await repo.UpdateAsync(template);

        var result = await ctx.PolicyTemplates.FindAsync(template.Id);
        result!.Name.Should().Be("Updated");
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletesTemplate()
    {
        using var ctx = CreateContext();
        var repo = new PolicyTemplateRepository(ctx);

        var template = CreateTemplate();
        await repo.AddAsync(template);

        await repo.DeleteAsync(template.Id);

        var result = await ctx.PolicyTemplates.FindAsync(template.Id);
        result.Should().NotBeNull();
        result!.IsActive.Should().BeFalse();

        // Should not appear in GetAllAsync
        var all = await repo.GetAllAsync();
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_NonexistentId_DoesNotThrow()
    {
        using var ctx = CreateContext();
        var repo = new PolicyTemplateRepository(ctx);

        var act = async () => await repo.DeleteAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetAllAsync_OrderedByName()
    {
        using var ctx = CreateContext();
        var repo = new PolicyTemplateRepository(ctx);

        await repo.AddAsync(CreateTemplate("Zebra", "zebra"));
        await repo.AddAsync(CreateTemplate("Alpha", "alpha"));
        await repo.AddAsync(CreateTemplate("Middle", "middle"));

        var all = await repo.GetAllAsync();
        all.Should().HaveCount(3);
        all[0].Name.Should().Be("Alpha");
        all[1].Name.Should().Be("Middle");
        all[2].Name.Should().Be("Zebra");
    }
}
