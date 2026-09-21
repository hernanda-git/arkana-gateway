using Arkana.Domain.Entities;
using Arkana.Gateway.Api.Endpoints;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Gateway.Api.Tests.Endpoints;

public sealed class PolicyTemplateEndpointsTests
{
    private static GatewayDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase($"endpoint_test_{Guid.NewGuid()}")
            .Options;
        return new GatewayDbContext(opts);
    }

    private static PolicyTemplateRepository Repo(GatewayDbContext ctx) => new(ctx);

    private static int GetStatusCode(IResult result)
    {
        // Extract status code from IResult — StatusCodeResult has a StatusCode property
        var statusCodeProp = result.GetType().GetProperty("StatusCode");
        if (statusCodeProp is not null)
            return (int)statusCodeProp.GetValue(result)!;
        // Fallback: check for well-known result types
        if (result is NotFound<object>) return 404;
        if (result is Conflict<object>) return 409;
        if (result is BadRequest<object>) return 400;
        return 0;
    }

    [Fact]
    public async Task List_ReturnsOkWithTemplates()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        await repo.AddAsync(PolicyTemplate.Create("Active", "active", "desc", "{}"));
        var inactive = PolicyTemplate.Create("Inactive", "inactive", "desc", "{}");
        inactive.Deactivate();
        await repo.AddAsync(inactive);

        var result = await PolicyTemplateEndpoints.ListTemplates(repo);
        GetStatusCode(result).Should().Be(200);
    }

    [Fact]
    public async Task GetBySlug_Existing_ReturnsOk()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        await repo.AddAsync(PolicyTemplate.Create("GDPR", "gdpr", "EU", "{}"));

        var result = await PolicyTemplateEndpoints.GetBySlug("gdpr", repo);
        GetStatusCode(result).Should().Be(200);
    }

    [Fact]
    public async Task GetBySlug_NotFound_Returns404()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);

        var result = await PolicyTemplateEndpoints.GetBySlug("nope", repo);
        GetStatusCode(result).Should().Be(404);
    }

    [Fact]
    public async Task Create_ValidTemplate_Returns201()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var req = new CreateTemplateRequest("PCI DSS", "pci-dss", "Payment security", """{"masking":"full"}""");

        var result = await PolicyTemplateEndpoints.CreateTemplate(req, repo);
        GetStatusCode(result).Should().Be(201);

        var stored = await repo.GetBySlugAsync("pci-dss");
        stored.Should().NotBeNull();
        stored!.Name.Should().Be("PCI DSS");
    }

    [Fact]
    public async Task Create_DuplicateSlug_Returns409()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        await repo.AddAsync(PolicyTemplate.Create("GDPR", "gdpr", "desc", "{}"));
        var req = new CreateTemplateRequest("GDPR v2", "gdpr", "desc", "{}");

        var result = await PolicyTemplateEndpoints.CreateTemplate(req, repo);
        GetStatusCode(result).Should().Be(409);
    }

    [Fact]
    public async Task Create_AutoSlugFromName()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var req = new CreateTemplateRequest("My Template", null, "desc", "{}");

        var result = await PolicyTemplateEndpoints.CreateTemplate(req, repo);
        GetStatusCode(result).Should().Be(201);

        var stored = await repo.GetBySlugAsync("my-template");
        stored.Should().NotBeNull();
    }

    [Fact]
    public async Task Update_NonBuiltin_Returns200()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var template = PolicyTemplate.Create("Custom", "custom", "old", "{}");
        await repo.AddAsync(template);

        var req = new UpdateTemplateRequest("Updated", "new desc", """{"v":2}""");
        var result = await PolicyTemplateEndpoints.UpdateTemplate(template.Id, req, repo);
        GetStatusCode(result).Should().Be(200);

        var updated = await repo.GetByIdAsync(template.Id);
        updated!.Name.Should().Be("Updated");
    }

    [Fact]
    public async Task Update_Builtin_Returns400()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var template = PolicyTemplate.Create("GDPR", "gdpr", "desc", "{}", isBuiltin: true);
        await repo.AddAsync(template);

        var req = new UpdateTemplateRequest("Hacked", null, null);
        var result = await PolicyTemplateEndpoints.UpdateTemplate(template.Id, req, repo);
        GetStatusCode(result).Should().Be(400);
    }

    [Fact]
    public async Task Deactivate_Returns200()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var template = PolicyTemplate.Create("Test", "test", "desc", "{}");
        await repo.AddAsync(template);

        var result = await PolicyTemplateEndpoints.DeactivateTemplate(template.Id, repo);
        GetStatusCode(result).Should().Be(200);

        var updated = await repo.GetByIdAsync(template.Id);
        updated!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_NonBuiltin_Returns200()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var template = PolicyTemplate.Create("Custom", "custom", "desc", "{}");
        await repo.AddAsync(template);

        var result = await PolicyTemplateEndpoints.DeleteTemplate(template.Id, repo);
        GetStatusCode(result).Should().Be(200);
    }

    [Fact]
    public async Task Delete_Builtin_Returns400()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var template = PolicyTemplate.Create("GDPR", "gdpr", "desc", "{}", isBuiltin: true);
        await repo.AddAsync(template);

        var result = await PolicyTemplateEndpoints.DeleteTemplate(template.Id, repo);
        GetStatusCode(result).Should().Be(400);
    }
}
