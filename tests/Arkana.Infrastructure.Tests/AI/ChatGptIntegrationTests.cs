using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.AI;
using Arkana.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Arkana.Infrastructure.Tests.AI;

public sealed class ChatGptIntegrationTests
{
    private static readonly Guid TestTenantId = Guid.NewGuid();

    [Fact]
    public void ChatGptEndpoints_RoundTrips_ThroughJson()
    {
        var eps = new ChatGptEndpoints(
            InferenceBaseUrl: "https://chatgpt.com/backend-api/codex",
            DeviceUsercodeUrl: "https://auth.openai.com/api/accounts/deviceauth/usercode",
            DeviceTokenUrl: "https://auth.openai.com/api/accounts/deviceauth/token");

        var json = eps.ToJson();
        var parsed = ChatGptEndpoints.Parse(json);

        parsed.InferenceBaseUrl.Should().Be(eps.InferenceBaseUrl);
        parsed.DeviceUsercodeUrl.Should().Be(eps.DeviceUsercodeUrl);
        parsed.DeviceTokenUrl.Should().Be(eps.DeviceTokenUrl);
    }

    [Fact]
    public void ChatGptEndpoints_Parse_HandlesNull()
    {
        var parsed = ChatGptEndpoints.Parse(null);
        parsed.InferenceBaseUrl.Should().Be("https://chatgpt.com/backend-api/codex");
    }

    [Fact]
    public async Task AccountPool_SelectsConnectedAccount_AndSkipsThrottled()
    {
        // Arrange: two chatgpt-accN providers, one throttled.
        var catalog = Substitute.For<IProviderCatalog>();
        catalog.GetAllAsync(TestTenantId, Arg.Any<CancellationToken>()).Returns(new List<AiProvider>
        {
            MakeProvider("chatgpt-acc1", enabled: true),
            MakeProvider("chatgpt-acc2", enabled: true),
        });

        var oauth = Substitute.For<IOAuthFlowService>();
        oauth.GetValidAccessTokenForTenantAsync(Arg.Any<Guid>(), TestTenantId, Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Guid>(0) == acc2Id ? null! : "tok-acc1");

        var pool = new ChatGptAccountPool(catalog, oauth, NullLogger<ChatGptAccountPool>.Instance);

        var selected = await pool.SelectAsync(TestTenantId);

        selected.Should().NotBeNull();
        selected!.Code.Should().Be("chatgpt-acc1");
    }

    [Fact]
    public async Task AccountPool_SelectsPinnedAccountBeforeRoundRobin()
    {
        var acc1 = MakeProvider("chatgpt-acc1", enabled: true);
        var acc2 = MakeProvider("chatgpt-acc2", enabled: true);
        var catalog = Substitute.For<IProviderCatalog>();
        catalog.GetAllAsync(TestTenantId, Arg.Any<CancellationToken>()).Returns(new List<AiProvider> { acc1, acc2 });
        var oauth = Substitute.For<IOAuthFlowService>();
        oauth.GetValidAccessTokenForTenantAsync(Arg.Any<Guid>(), TestTenantId, Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Guid>(0) == acc2.Id ? "tok-acc2" : "tok-acc1");

        var pool = new ChatGptAccountPool(catalog, oauth, NullLogger<ChatGptAccountPool>.Instance);

        var selected = await pool.SelectAsync(TestTenantId, "CHATGPT-ACC2");

        selected.Should().NotBeNull();
        selected!.Code.Should().Be("chatgpt-acc2");
    }

    [Fact]
    public async Task AccountPool_StrictPinReturnsNullWhenPinnedAccountUnavailable()
    {
        var acc1 = MakeProvider("chatgpt-acc1", enabled: true);
        var acc2 = MakeProvider("chatgpt-acc2", enabled: true);
        var catalog = Substitute.For<IProviderCatalog>();
        catalog.GetAllAsync(TestTenantId, Arg.Any<CancellationToken>()).Returns(new List<AiProvider> { acc1, acc2 });
        var oauth = Substitute.For<IOAuthFlowService>();
        oauth.GetValidAccessTokenForTenantAsync(Arg.Any<Guid>(), TestTenantId, Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Guid>(0) == acc2.Id ? null! : "tok-acc1");

        var pool = new ChatGptAccountPool(catalog, oauth, NullLogger<ChatGptAccountPool>.Instance);

        var selected = await pool.SelectAsync(TestTenantId, "chatgpt-acc2", allowFallback: false);

        selected.Should().BeNull();
    }

    [Fact]
    public async Task AccountPool_ExplicitFallbackUsesAnotherAccountWhenPinnedAccountUnavailable()
    {
        var acc1 = MakeProvider("chatgpt-acc1", enabled: true);
        var acc2 = MakeProvider("chatgpt-acc2", enabled: true);
        var catalog = Substitute.For<IProviderCatalog>();
        catalog.GetAllAsync(TestTenantId, Arg.Any<CancellationToken>()).Returns(new List<AiProvider> { acc1, acc2 });
        var oauth = Substitute.For<IOAuthFlowService>();
        oauth.GetValidAccessTokenForTenantAsync(Arg.Any<Guid>(), TestTenantId, Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Guid>(0) == acc2.Id ? null! : "tok-acc1");

        var pool = new ChatGptAccountPool(catalog, oauth, NullLogger<ChatGptAccountPool>.Instance);

        var selected = await pool.SelectAsync(TestTenantId, "chatgpt-acc2", allowFallback: true);

        selected.Should().NotBeNull();
        selected!.Code.Should().Be("chatgpt-acc1");
    }

    [Fact]
    public async Task AccountPool_ReturnsNull_WhenNoAccountAvailable()
    {
        var catalog = Substitute.For<IProviderCatalog>();
        catalog.GetAllAsync(TestTenantId, Arg.Any<CancellationToken>()).Returns(new List<AiProvider>
        {
            MakeProvider("chatgpt-acc1", enabled: true),
        });

        var oauth = Substitute.For<IOAuthFlowService>();
        oauth.GetValidAccessTokenForTenantAsync(Arg.Any<Guid>(), TestTenantId, Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var pool = new ChatGptAccountPool(catalog, oauth, NullLogger<ChatGptAccountPool>.Instance);

        var selected = await pool.SelectAsync(TestTenantId);
        selected.Should().BeNull();
    }

    [Fact]
    public void OpenAiDevicePendingError_IsNestedErrorCode()
    {
        const string pendingJson = """
            {"error":{"message":"Device authorization is pending. Please try again.","type":"invalid_request_error","param":null,"code":"deviceauth_authorization_pending"}}
            """;
        using var doc = System.Text.Json.JsonDocument.Parse(pendingJson);
        var err = doc.RootElement.GetProperty("error");
        err.GetProperty("code").GetString().Should().Be("deviceauth_authorization_pending");
    }

    private static Guid acc2Id = Guid.NewGuid();
    private static AiProvider MakeProvider(string code, bool enabled)
    {
        if (code == "chatgpt-acc2") return Make("chatgpt-acc2", enabled, acc2Id);
        return Make("chatgpt-acc1", enabled, Guid.NewGuid());
    }
    private static AiProvider Make(string code, bool enabled, Guid id)
    {
        var p = AiProvider.Create(name: code, code: code, priority: 1, baseUrl: "https://chatgpt.com/backend-api");
        typeof(AiProvider).GetProperty(nameof(AiProvider.Id))!.SetValue(p, id);
        if (!enabled) p.Disable();
        return p;
    }
}
