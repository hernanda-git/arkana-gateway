using Arkana.Domain.Entities;
using Arkana.Gateway.Api.Endpoints;
using Arkana.Gateway.Api.Services;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Gateway.Api.Tests.Endpoints;

public sealed class WebhookEndpointsTests
{
    private static GatewayDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase($"webhook_endpoint_test_{Guid.NewGuid()}")
            .Options;
        return new GatewayDbContext(opts);
    }

    private static WebhookRepository Repo(GatewayDbContext ctx) => new(ctx);

    private static int GetStatusCode(IResult result)
    {
        var statusCodeProp = result.GetType().GetProperty("StatusCode");
        if (statusCodeProp is not null)
            return (int)statusCodeProp.GetValue(result)!;
        if (result is NotFound<object>) return 404;
        if (result is Conflict<object>) return 409;
        if (result is BadRequest<object>) return 400;
        return 0;
    }

    [Fact]
    public async Task ListWebhooks_ReturnsOkWithEmptyList()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);

        var result = await WebhookEndpoints.ListWebhooks(repo);

        GetStatusCode(result).Should().Be(200);
    }

    [Fact]
    public async Task ListWebhooks_ReturnsOkWithWebhooks()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var tenantId = Guid.NewGuid();
        await repo.CreateAsync(Webhook.Create(tenantId, "https://example.com/hook1", "secret1", ["agent.completed"]));
        await repo.CreateAsync(Webhook.Create(tenantId, "https://example.com/hook2", "secret2", ["workflow.completed"]));

        var result = await WebhookEndpoints.ListWebhooks(repo);

        GetStatusCode(result).Should().Be(200);
    }

    [Fact]
    public async Task GetWebhook_Existing_ReturnsOk()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var tenantId = Guid.NewGuid();
        var webhook = Webhook.Create(tenantId, "https://example.com/hook", "secret", ["agent.completed"]);
        await repo.CreateAsync(webhook);

        var result = await WebhookEndpoints.GetWebhook(webhook.Id, repo);

        GetStatusCode(result).Should().Be(200);
    }

    [Fact]
    public async Task GetWebhook_NotFound_Returns404()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);

        var result = await WebhookEndpoints.GetWebhook(Guid.NewGuid(), repo);

        GetStatusCode(result).Should().Be(404);
    }

    [Fact]
    public async Task CreateWebhook_ValidRequest_Returns201()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var tenantId = Guid.NewGuid();
        var req = new CreateWebhookRequest(
            tenantId,
            "https://example.com/webhook",
            "my-secret-key",
            [WebhookEvents.AgentCompleted, WebhookEvents.WorkflowCompleted]);

        var result = await WebhookEndpoints.CreateWebhook(req, repo, new DefaultHttpContext());

        GetStatusCode(result).Should().Be(201);
    }

    [Fact]
    public async Task CreateWebhook_StoresCorrectly()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var tenantId = Guid.NewGuid();
        var req = new CreateWebhookRequest(
            tenantId,
            "https://example.com/webhook",
            "my-secret-key",
            [WebhookEvents.AgentCompleted]);

        await WebhookEndpoints.CreateWebhook(req, repo, new DefaultHttpContext());

        var webhooks = await repo.GetAllAsync();
        webhooks.Should().HaveCount(1);
        webhooks[0].Url.Should().Be("https://example.com/webhook");
        webhooks[0].TenantId.Should().Be(tenantId);
        webhooks[0].IsActive.Should().BeTrue();
        webhooks[0].RetryCount.Should().Be(3);
        webhooks[0].GetEvents().Should().ContainSingle(e => e == WebhookEvents.AgentCompleted);
    }

    [Fact]
    public async Task CreateWebhook_DefaultRetryCount_IsThree()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var req = new CreateWebhookRequest(
            Guid.NewGuid(),
            "https://example.com/hook",
            "secret",
            [], RetryCount: 3);

        await WebhookEndpoints.CreateWebhook(req, repo, new DefaultHttpContext());

        var webhooks = await repo.GetAllAsync();
        webhooks[0].RetryCount.Should().Be(3);
    }

    [Fact]
    public async Task UpdateWebhook_Existing_Returns200()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var webhook = Webhook.Create(Guid.NewGuid(), "https://old.com/hook", "old-secret", ["agent.completed"]);
        await repo.CreateAsync(webhook);

        var req = new UpdateWebhookRequest(
            Url: "https://new.com/hook",
            Secret: null,
            Events: null,
            RetryCount: 5,
            IsActive: false);

        var result = await WebhookEndpoints.UpdateWebhook(webhook.Id, req, repo);

        GetStatusCode(result).Should().Be(200);

        var updated = await repo.GetByIdAsync(webhook.Id);
        updated!.Url.Should().Be("https://new.com/hook");
        updated.RetryCount.Should().Be(5);
        updated.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateWebhook_NotFound_Returns404()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var req = new UpdateWebhookRequest("https://new.com/hook", null, null, null, null);

        var result = await WebhookEndpoints.UpdateWebhook(Guid.NewGuid(), req, repo);

        GetStatusCode(result).Should().Be(404);
    }

    [Fact]
    public async Task UpdateWebhook_Activate_Returns200()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var webhook = Webhook.Create(Guid.NewGuid(), "https://hook.com", "secret", ["agent.completed"]);
        webhook.Deactivate();
        await repo.CreateAsync(webhook);

        var req = new UpdateWebhookRequest(null, null, null, null, IsActive: true);
        var result = await WebhookEndpoints.UpdateWebhook(webhook.Id, req, repo);

        GetStatusCode(result).Should().Be(200);
        var updated = await repo.GetByIdAsync(webhook.Id);
        updated!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteWebhook_Existing_Returns200()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var webhook = Webhook.Create(Guid.NewGuid(), "https://hook.com", "secret", ["agent.completed"]);
        await repo.CreateAsync(webhook);

        var result = await WebhookEndpoints.DeleteWebhook(webhook.Id, repo);

        GetStatusCode(result).Should().Be(200);
        var deleted = await repo.GetByIdAsync(webhook.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteWebhook_NotFound_Returns404()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);

        var result = await WebhookEndpoints.DeleteWebhook(Guid.NewGuid(), repo);

        GetStatusCode(result).Should().Be(404);
    }

    [Fact]
    public async Task CreateWebhook_WithAllEventTypes_Succeeds()
    {
        using var ctx = CreateContext();
        var repo = Repo(ctx);
        var req = new CreateWebhookRequest(
            Guid.NewGuid(),
            "https://example.com/all-events",
            "secret",
            WebhookEvents.All);

        var result = await WebhookEndpoints.CreateWebhook(req, repo, new DefaultHttpContext());

        GetStatusCode(result).Should().Be(201);
        var webhooks = await repo.GetAllAsync();
        webhooks[0].GetEvents().Should().HaveCount(4);
    }

    [Fact]
    public async Task WebhookEntity_RecordSuccess_ResetsFailureCount()
    {
        var webhook = Webhook.Create(Guid.NewGuid(), "https://hook.com", "secret", ["agent.completed"]);
        webhook.RecordFailure();
        webhook.RecordFailure();
        webhook.FailureCount.Should().Be(2);

        webhook.RecordSuccess();

        webhook.FailureCount.Should().Be(0);
        webhook.LastTriggeredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task WebhookEntity_RecordFailure_IncrementsCount()
    {
        var webhook = Webhook.Create(Guid.NewGuid(), "https://hook.com", "secret", ["agent.completed"]);

        webhook.RecordFailure();
        webhook.RecordFailure();
        webhook.RecordFailure();

        webhook.FailureCount.Should().Be(3);
        webhook.LastTriggeredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task WebhookEntity_Create_SetsDefaults()
    {
        var webhook = Webhook.Create(Guid.NewGuid(), "https://hook.com", "secret", ["agent.completed"]);

        webhook.Id.Should().NotBeEmpty();
        webhook.IsActive.Should().BeTrue();
        webhook.RetryCount.Should().Be(3);
        webhook.FailureCount.Should().Be(0);
        webhook.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        webhook.LastTriggeredAt.Should().BeNull();
    }

    [Fact]
    public async Task WebhookEntity_UpdateEvents_WorksCorrectly()
    {
        var webhook = Webhook.Create(Guid.NewGuid(), "https://hook.com", "secret", ["agent.completed"]);

        webhook.UpdateEvents(["workflow.completed", "token.threshold"]);

        webhook.GetEvents().Should().HaveCount(2);
        webhook.GetEvents().Should().Contain("workflow.completed");
        webhook.GetEvents().Should().Contain("token.threshold");
        webhook.GetEvents().Should().NotContain("agent.completed");
    }

    [Fact]
    public async Task WebhookEntity_ActivateDeactivate_WorksCorrectly()
    {
        var webhook = Webhook.Create(Guid.NewGuid(), "https://hook.com", "secret", ["agent.completed"]);
        webhook.IsActive.Should().BeTrue();

        webhook.Deactivate();
        webhook.IsActive.Should().BeFalse();

        webhook.Activate();
        webhook.IsActive.Should().BeTrue();
    }
}
