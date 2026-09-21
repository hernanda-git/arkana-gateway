using Arkana.Domain.Entities;
using Arkana.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Tests.Persistence;

public sealed class PolicyTemplateSeedTests
{
    [Fact]
    public async Task SeedData_Contains5BuiltinTemplates()
    {
        var opts = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase($"seed_test_{Guid.NewGuid()}")
            .Options;

        // Use a separate context to apply seed data
        using var seedCtx = new GatewayDbContext(opts);
        seedCtx.Database.EnsureCreated();

        var templates = await seedCtx.PolicyTemplates.ToListAsync();

        templates.Should().HaveCount(5);
        templates.Should().OnlyContain(t => t.IsBuiltin);
        templates.Should().OnlyContain(t => t.IsActive);
    }

    [Fact]
    public async Task SeedData_HasGDPR()
    {
        using var ctx = CreateSeededContext();

        var gdpr = await ctx.PolicyTemplates.FirstOrDefaultAsync(t => t.Slug == "gdpr");
        gdpr.Should().NotBeNull();
        gdpr!.Name.Should().Be("GDPR");
        gdpr.Config.Should().Contain("pii_detection");
        gdpr.Config.Should().Contain("365");
    }

    [Fact]
    public async Task SeedData_HasPIIStrict()
    {
        using var ctx = CreateSeededContext();

        var t = await ctx.PolicyTemplates.FirstOrDefaultAsync(t => t.Slug == "pii-strict");
        t.Should().NotBeNull();
        t!.Config.Should().Contain("credit_card");
    }

    [Fact]
    public async Task SeedData_HasComplianceAudit()
    {
        using var ctx = CreateSeededContext();

        var t = await ctx.PolicyTemplates.FirstOrDefaultAsync(t => t.Slug == "compliance-audit");
        t.Should().NotBeNull();
        t!.Config.Should().Contain("audit");
    }

    [Fact]
    public async Task SeedData_HasPublicChatbot()
    {
        using var ctx = CreateSeededContext();

        var t = await ctx.PolicyTemplates.FirstOrDefaultAsync(t => t.Slug == "public-chatbot");
        t.Should().NotBeNull();
        t!.Config.Should().Contain("topic_allow_list");
    }

    [Fact]
    public async Task SeedData_HasIndonesiaPII()
    {
        using var ctx = CreateSeededContext();

        var t = await ctx.PolicyTemplates.FirstOrDefaultAsync(t => t.Slug == "indonesia-pii");
        t.Should().NotBeNull();
        t!.Config.Should().Contain("nik");
        t!.Config.Should().Contain("npwp");
    }

    [Fact]
    public async Task SeedData_AllSlugsAreUnique()
    {
        using var ctx = CreateSeededContext();

        var slugs = await ctx.PolicyTemplates.Select(t => t.Slug).ToListAsync();
        slugs.Should().OnlyHaveUniqueItems();
    }

    private static GatewayDbContext CreateSeededContext()
    {
        var opts = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase($"seed_verify_{Guid.NewGuid()}")
            .Options;
        var ctx = new GatewayDbContext(opts);
        ctx.Database.EnsureCreated();
        return ctx;
    }
}
