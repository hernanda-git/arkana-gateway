using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Infrastructure.OAuth;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Repositories;
using Arkana.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Arkana.Infrastructure.Tests.OAuth;

/// <summary>
/// Integration test for the gateway-managed OAuth authorization-code + PKCE flow
/// against a stubbed token endpoint. Verifies: flow start (real
/// authorize URL + PKCE state persisted), server-side callback exchange, sealed
/// token storage, provider flip to OAuth, status reporting, bearer resolution,
/// and background refresh. Employee-side secret entry is never exercised — the
/// client id/secret are platform-held server-side.
/// </summary>
public sealed class OAuthFlowServiceTests
{
    private static readonly byte[] TestMasterKey = new byte[]
    {
        0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10,
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00
    };

    private static GatewayDbContext CreateDb(string? databaseName = null) =>
        new(new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString()).Options);

    private static EnvelopeCredentialVault Vault() => new(TestMasterKey);

    /// <summary>Serves the authorization-code → token exchange. Captures the posted body.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _tokenJson;
        public string? LastPostedBody { get; private set; }
        public int Calls { get; private set; }
        public StubHandler(string tokenJson) => _tokenJson = tokenJson;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.Content is not null)
                LastPostedBody = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_tokenJson, Encoding.UTF8, "application/json")
            };
        }
    }

    private static IHttpClientFactory MakeFactory(HttpMessageHandler handler)
    {
        var mock = Substitute.For<IHttpClientFactory>();
        mock.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") });
        return mock;
    }

    private static OAuthFlowService MakeService(GatewayDbContext db, EnvelopeCredentialVault vault,
        Guid tenantId, HttpMessageHandler handler)
        => MakeService(db, vault, new StaticTenantProvider(tenantId), handler);

    private static OAuthFlowService MakeService(GatewayDbContext db, EnvelopeCredentialVault vault,
        ITenantProvider tenant, HttpMessageHandler handler)
        => new(
            new ProviderOAuthTokenRepository(db),
            new OAuthProviderConfigRepository(db),
            new OAuthPendingFlowRepository(db),
            new AiProviderRepository(db),
            vault,
            tenant,
            MakeFactory(handler),
            NullLogger<OAuthFlowService>.Instance);

    [Fact]
    public async Task AuthorizationCode_Flow_Connects_And_Stores_Sealed_Tokens()
    {
        // Arrange
        using var db = CreateDb();
        var vault = Vault();
        var tenant = Tenant.Create("Test", "acme", "{}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var cfg = OAuthProviderConfig.Create("gemini", "Gemini", OAuthGrant.AuthorizationCode,
            "https://example.test/oauth/token", "gw_client_id",
            authorizationEndpoint: "https://example.test/oauth/authorize",
            clientSecretPlaintext: "client=secret==",
            vault: vault,
            scopes: "openid");
        db.OAuthProviderConfigs.Add(cfg);

        var provider = AiProvider.Create("Gemini", "gemini", 5, baseUrl: "https://generativelanguage.googleapis.com/v1");
        provider.AssignTenant(tenant.Id);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync();

        var tokenJson = JsonSerializer.Serialize(new
        {
            access_token = "eyJ.access.token",
            token_type = "Bearer",
            expires_in = 3600,
            refresh_token = "refresh_abc"
        });

        var handler = new StubHandler(tokenJson);
        var svc = MakeService(db, vault, tenant.Id, handler);

        // Act — start the flow (returns a real authorize URL)
        var start = await svc.StartAsync("gemini", "https://gateway.test");

        // Assert authorize URL shape (PKCE + redirect_uri + state)
        Assert.Contains("code_challenge=", start.AuthorizationUrl);
        Assert.Contains("redirect_uri=", start.AuthorizationUrl);
        Assert.Contains("state=" + start.State, start.AuthorizationUrl);
        Assert.Contains("redirect_uri=https%3A%2F%2Fgateway.test%2Foauth%2Fgemini%2Fcallback", start.AuthorizationUrl);

        // Pending flow persisted (survives restart mid-flow)
        var pending = await db.OAuthPendingFlows.SingleAsync(f => f.State == start.State);
        Assert.Equal("gemini", pending.ProviderCode);
        Assert.Equal("https://gateway.test/oauth/gemini/callback", pending.RedirectUri);

        // Act — provider redirects back; gateway exchanges server-side
        var result = await svc.HandleCallbackAsync("gemini", "authz_code_123==", start.State);

        // Assert exchange + sealed storage
        Assert.Equal(OAuthTokenStatus.Connected, result.Status);
        Assert.Contains("code_verifier=", handler.LastPostedBody);     // PKCE verifier sent
        Assert.Contains("code=authz_code_123%3D%3D", handler.LastPostedBody);
        Assert.Contains("client_secret=client%3Dsecret%3D%3D", handler.LastPostedBody);
        Assert.Contains("redirect_uri=https%3A%2F%2Fgateway.test%2Foauth%2Fgemini%2Fcallback", handler.LastPostedBody);

        var token = await db.ProviderOAuthTokens.SingleAsync(t => t.AiProviderId == provider.Id);
        Assert.StartsWith("v1:", token.SealedAccessToken);              // sealed, not plaintext
        Assert.Equal("eyJ.access.token", vault.Open(token.SealedAccessToken));

        // Provider flipped to OAuth so the connector resolves the bearer
        var reloaded = await db.AiProviders.SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(AuthMethod.OAuth, reloaded.AuthMethod);
        Assert.Equal(cfg.Id, reloaded.OAuthConfigId);

        // Connector-resolvable bearer matches plaintext
        Assert.Equal("eyJ.access.token", await svc.GetValidAccessTokenAsync(provider.Id));

        // Status reporting
        var connStatus = await svc.GetStatusAsync("gemini");
        Assert.Equal(OAuthTokenStatus.Connected, connStatus.Status);
        Assert.True(connStatus.HasRefreshToken);
    }

    [Fact]
    public async Task Callback_With_Bad_State_Is_Rejected()
    {
        using var db = CreateDb();
        var vault = Vault();
        var tenant = Tenant.Create("Test", "acme", "{}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        db.OAuthProviderConfigs.Add(OAuthProviderConfig.Create("gemini", "Gemini", OAuthGrant.AuthorizationCode,
            "https://example.test/oauth/token", "cid",
            authorizationEndpoint: "https://example.test/oauth/authorize"));
        db.AiProviders.Add(AiProvider.Create("Gemini", "gemini", 5));
        await db.SaveChangesAsync();

        var svc = MakeService(db, vault, tenant.Id, new StubHandler("{}"));
        var result = await svc.HandleCallbackAsync("gemini", "code", "unknown-state");

        Assert.Equal(OAuthTokenStatus.Error, result.Status);
        Assert.DoesNotContain("OAuthProviderConfig", result.ErrorMessage ?? "");
    }

    [Fact]
    public async Task StartAsync_WithoutTenant_FailsClosed()
    {
        using var db = CreateDb();
        var svc = MakeService(db, Vault(), new NullTenantProvider(), new StubHandler("{}"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.StartAsync("gemini", "https://gateway.test"));
    }

    [Fact]
    public async Task AuthorizationCode_RetryFinalizesDurableCompletionWithoutReexchange()
    {
        using var db = CreateDb();
        var vault = Vault();
        var tenant = Tenant.Create("Test", "acme", "{}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var cfg = OAuthProviderConfig.Create("gemini", "Gemini", OAuthGrant.AuthorizationCode,
            "https://example.test/oauth/token", "gw_client_id",
            authorizationEndpoint: "https://example.test/oauth/authorize");
        db.OAuthProviderConfigs.Add(cfg);
        var provider = AiProvider.Create("Gemini", "gemini", 5);
        provider.AssignTenant(tenant.Id);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync();

        var tokenJson = JsonSerializer.Serialize(new
        {
            access_token = "one-time.access",
            token_type = "Bearer",
            expires_in = 3600,
            refresh_token = "one-time.refresh"
        });
        var handler = new StubHandler(tokenJson);
        var realPending = new OAuthPendingFlowRepository(db);
        var failingPending = new FailingDeletePendingFlowRepository(realPending);
        var firstService = new OAuthFlowService(
            new ProviderOAuthTokenRepository(db),
            new OAuthProviderConfigRepository(db),
            failingPending,
            new AiProviderRepository(db),
            vault,
            new StaticTenantProvider(tenant.Id),
            MakeFactory(handler),
            NullLogger<OAuthFlowService>.Instance);

        var start = await firstService.StartAsync("gemini", "https://gateway.test");
        var first = await firstService.HandleCallbackAsync("gemini", "one-time-code", start.State);

        Assert.Equal(OAuthTokenStatus.Error, first.Status);
        Assert.Equal(1, handler.Calls);
        var pending = await db.OAuthPendingFlows.SingleAsync(f => f.State == start.State);
        Assert.True(pending.HasCompletionTokens);
        Assert.NotNull(pending.ClaimedAt);

        var retryService = MakeService(db, vault, tenant.Id, handler);
        var retry = await retryService.HandleCallbackAsync("gemini", "one-time-code", start.State);

        Assert.Equal(OAuthTokenStatus.Connected, retry.Status);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(await db.OAuthPendingFlows.ToListAsync());
        var stored = await db.ProviderOAuthTokens.SingleAsync(t => t.AiProviderId == provider.Id);
        Assert.Equal("one-time.access", vault.Open(stored.SealedAccessToken));
    }

    [Fact]
    public async Task AuthorizationCode_TokenPersistenceFailureRecoversFromSealedPendingStateWithoutReexchange()
    {
        var databaseName = Guid.NewGuid().ToString();
        using var db = CreateDb(databaseName);
        var vault = Vault();
        var tenant = Tenant.Create("Test", "acme", "{}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var cfg = OAuthProviderConfig.Create("gemini", "Gemini", OAuthGrant.AuthorizationCode,
            "https://example.test/oauth/token", "gw_client_id",
            authorizationEndpoint: "https://example.test/oauth/authorize");
        db.OAuthProviderConfigs.Add(cfg);
        var provider = AiProvider.Create("Gemini", "gemini", 5);
        provider.AssignTenant(tenant.Id);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync();

        var tokenJson = JsonSerializer.Serialize(new
        {
            access_token = "durable.access",
            token_type = "Bearer",
            expires_in = 3600,
            refresh_token = "durable.refresh"
        });
        var handler = new StubHandler(tokenJson);
        var failingTokens = new FailingTokenRepository(new ProviderOAuthTokenRepository(db), failAdd: true);
        var firstService = new OAuthFlowService(
            failingTokens,
            new OAuthProviderConfigRepository(db),
            new OAuthPendingFlowRepository(db),
            new AiProviderRepository(db),
            vault,
            new StaticTenantProvider(tenant.Id),
            MakeFactory(handler),
            NullLogger<OAuthFlowService>.Instance);

        var start = await firstService.StartAsync("gemini", "https://gateway.test");
        var first = await firstService.HandleCallbackAsync("gemini", "one-time-code", start.State);

        Assert.Equal(OAuthTokenStatus.Error, first.Status);
        Assert.Equal(1, handler.Calls);
        var pending = await db.OAuthPendingFlows.SingleAsync(f => f.State == start.State);
        Assert.True(pending.HasCompletionTokens);
        Assert.NotNull(pending.ClaimedAt);

        var retry = await MakeService(db, vault, tenant.Id, handler)
            .HandleCallbackAsync("gemini", "one-time-code", start.State);

        Assert.Equal(OAuthTokenStatus.Connected, retry.Status);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(await db.OAuthPendingFlows.ToListAsync());
    }

    [Fact]
    public async Task AuthorizationCode_PendingUpdateFailureRecoversFromLinkedTokenWithoutReexchange()
    {
        var databaseName = Guid.NewGuid().ToString();
        using var db = CreateDb(databaseName);
        var vault = Vault();
        var tenant = Tenant.Create("Test", "acme", "{}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var cfg = OAuthProviderConfig.Create("gemini", "Gemini", OAuthGrant.AuthorizationCode,
            "https://example.test/oauth/token", "gw_client_id",
            authorizationEndpoint: "https://example.test/oauth/authorize");
        db.OAuthProviderConfigs.Add(cfg);
        var provider = AiProvider.Create("Gemini", "gemini", 5);
        provider.AssignTenant(tenant.Id);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync();

        var tokenJson = JsonSerializer.Serialize(new
        {
            access_token = "linked.access",
            token_type = "Bearer",
            expires_in = 3600,
            refresh_token = "linked.refresh"
        });
        var handler = new StubHandler(tokenJson);
        var realPending = new OAuthPendingFlowRepository(db);
        var failingPending = new FailingDeletePendingFlowRepository(realPending, failUpdate: true);
        var firstService = new OAuthFlowService(
            new ProviderOAuthTokenRepository(db),
            new OAuthProviderConfigRepository(db),
            failingPending,
            new AiProviderRepository(db),
            vault,
            new StaticTenantProvider(tenant.Id),
            MakeFactory(handler),
            NullLogger<OAuthFlowService>.Instance);

        var start = await firstService.StartAsync("gemini", "https://gateway.test");
        var first = await firstService.HandleCallbackAsync("gemini", "one-time-code", start.State);

        Assert.Equal(OAuthTokenStatus.Error, first.Status);
        Assert.Equal(1, handler.Calls);
        using var checkDb = CreateDb(databaseName);
        var pending = await checkDb.OAuthPendingFlows.SingleAsync(f => f.State == start.State);
        Assert.True(pending.HasCompletionTokens);
        Assert.NotNull(pending.ClaimedAt);
        var linked = await checkDb.ProviderOAuthTokens.SingleAsync(t => t.AiProviderId == provider.Id);
        Assert.Equal(pending.Id, linked.CompletionFlowId);

        using var retryDb = CreateDb(databaseName);
        var retry = await MakeService(retryDb, vault, tenant.Id, handler)
            .HandleCallbackAsync("gemini", "one-time-code", start.State);

        Assert.Equal(OAuthTokenStatus.Connected, retry.Status);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(await retryDb.OAuthPendingFlows.ToListAsync());
    }

    [Fact]
    public async Task ExpiredCompletionStateCannotBeRecoveredOrReplayed()
    {
        using var db = CreateDb();
        var vault = Vault();
        var tenant = Tenant.Create("Test", "acme", "{}");
        db.Tenants.Add(tenant);
        var cfg = OAuthProviderConfig.Create("gemini", "Gemini", OAuthGrant.AuthorizationCode,
            "https://example.test/oauth/token", "gw_client_id",
            authorizationEndpoint: "https://example.test/oauth/authorize");
        db.OAuthProviderConfigs.Add(cfg);
        var provider = AiProvider.Create("Gemini", "gemini", 5);
        provider.AssignTenant(tenant.Id);
        db.AiProviders.Add(provider);
        var flow = OAuthPendingFlow.Create(
            tenant.Id,
            "gemini",
            "expired-state",
            "verifier",
            "https://gateway.test/oauth/gemini/callback",
            vault,
            TimeSpan.FromMinutes(-1),
            aiProviderCode: provider.Code);
        flow.TryClaim(DateTimeOffset.UtcNow.AddMinutes(-2));
        flow.StoreCompletionTokens("expired.access", "expired.refresh", "Bearer",
            DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2), vault);
        db.OAuthPendingFlows.Add(flow);
        await db.SaveChangesAsync();

        var handler = new StubHandler("{}");
        var service = MakeService(db, vault, tenant.Id, handler);

        var recovered = await service.RecoverCallbackAsync("gemini", flow.State);
        var replayed = await service.HandleCallbackAsync("gemini", "one-time-code", flow.State);

        Assert.Equal(OAuthTokenStatus.Error, recovered.Status);
        Assert.Equal(OAuthTokenStatus.Error, replayed.Status);
        Assert.Equal(0, handler.Calls);
        Assert.Single(await db.OAuthPendingFlows.ToListAsync());
    }

    [Fact]
    public async Task DeviceCompletionClaim_IsExclusive()
    {
        var databaseName = Guid.NewGuid().ToString();
        using var db = CreateDb(databaseName);
        var vault = Vault();
        var tenant = Tenant.Create("Test", "acme", "{}");
        db.Tenants.Add(tenant);
        var flow = OAuthPendingFlow.Create(
            tenant.Id,
            "chatgpt",
            "device-claim-state",
            "verifier",
            "https://example.test/deviceauth/callback",
            vault,
            TimeSpan.FromMinutes(10),
            aiProviderCode: "chatgpt-acc1",
            deviceAuthId: "device-id",
            deviceUserCode: "device-code");
        db.OAuthPendingFlows.Add(flow);
        await db.SaveChangesAsync();

        using var firstDb = CreateDb(databaseName);
        using var secondDb = CreateDb(databaseName);
        var firstClaim = await new OAuthPendingFlowRepository(firstDb)
            .TryClaimCompletionAsync(flow.Id, tenant.Id, DateTimeOffset.UtcNow);
        var secondClaim = await new OAuthPendingFlowRepository(secondDb)
            .TryClaimCompletionAsync(flow.Id, tenant.Id, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.True(firstClaim);
        Assert.False(secondClaim);
    }

    [Fact]
    public async Task Refresh_Updates_Access_Token_And_Keeps_Sealed()
    {
        using var db = CreateDb();
        var vault = Vault();
        var tenant = Tenant.Create("Test", "acme", "{}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        db.OAuthProviderConfigs.Add(OAuthProviderConfig.Create("gemini", "Gemini", OAuthGrant.AuthorizationCode,
            "https://example.test/oauth/token", "cid",
            authorizationEndpoint: "https://example.test/oauth/authorize"));
        var provider = AiProvider.Create("Gemini", "gemini", 5);
        provider.AssignTenant(tenant.Id);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync();

        var firstJson = JsonSerializer.Serialize(new
        {
            access_token = "first.token", token_type = "Bearer", expires_in = 3600, refresh_token = "refresh_abc"
        });
        var svc = MakeService(db, vault, tenant.Id, new StubHandler(firstJson));

        var start = await svc.StartAsync("gemini", "https://gateway.test");
        Assert.Equal(OAuthTokenStatus.Connected, (await svc.HandleCallbackAsync("gemini", "c", start.State)).Status);
        Assert.Equal("first.token", await svc.GetValidAccessTokenAsync(provider.Id));

        // Simulate expiry so the next bearer resolution triggers a refresh.
        var refreshedJson = JsonSerializer.Serialize(new
        {
            access_token = "second.token", token_type = "Bearer", expires_in = 3600, refresh_token = "refresh_abc"
        });
        var svc2 = MakeService(db, vault, tenant.Id, new StubHandler(refreshedJson));
        var stored = await db.ProviderOAuthTokens.SingleAsync(t => t.AiProviderId == provider.Id);
        stored.RefreshTokens(stored.SealedAccessToken!, stored.SealedRefreshToken, "Bearer",
            DateTimeOffset.UtcNow.AddMinutes(-10), stored.RefreshExpiresAt);
        await db.SaveChangesAsync();

        Assert.Equal("second.token", await svc2.GetValidAccessTokenAsync(provider.Id));
    }

    [Fact]
    public async Task StartAsync_DeviceCodeGrant_ReturnsDeviceCode_NotAuthorizeUrl()
    {
        // Regression: ChatGPT/Codex uses the device-code grant. StartAsync must NOT
        // build an /oauth/authorize?response_type=code URL (OpenAI rejects it with
        // invalid_authorize_request); it must return a user_code + verification URL.
        using var db = CreateDb();
        var vault = Vault();
        var tenant = Tenant.Create("Test", "acme", "{}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var endpoints = new ChatGptEndpoints(
            InferenceBaseUrl: "https://chatgpt.com/backend-api/codex",
            DeviceUsercodeUrl: "https://example.test/deviceauth/usercode",
            DeviceTokenUrl: "https://example.test/deviceauth/token");
        var cfg = OAuthProviderConfig.Create("chatgpt", "ChatGPT (Codex) OAuth", OAuthGrant.DeviceCode,
            "https://auth.openai.com/oauth/token", "app_testclient",
            authorizationEndpoint: "https://auth.openai.com/oauth/authorize",
            extraAuthParams: System.Text.Json.JsonSerializer.Serialize(endpoints));
        db.OAuthProviderConfigs.Add(cfg);
        var provider = AiProvider.Create("ChatGPT Account 1", "chatgpt-acc1", 5);
        provider.AssignTenant(tenant.Id);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync();
        provider.SetAuthMethod(AuthMethod.OAuth, cfg.Id);
        await db.SaveChangesAsync();

        // Stub OpenAI device usercode endpoint.
        var usercodeJson = JsonSerializer.Serialize(new
        {
            device_auth_id = "device_abc",
            user_code = "1ABC-2DEF",
            verification_uri = "https://auth.openai.com/codex/device"
        });
        var handler = new StubHandler(usercodeJson);
        var svc = MakeService(db, vault, tenant.Id, handler);

        var start = await svc.StartAsync("chatgpt-acc1", "https://gateway.test");

        Assert.True(start.IsDeviceCode);
        Assert.Equal(string.Empty, start.AuthorizationUrl);
        Assert.Equal("1ABC-2DEF", start.UserCode);
        Assert.Equal("https://auth.openai.com/codex/device", start.VerificationUrl);
        Assert.NotEmpty(start.State);
    }

    [Fact]
    public async Task Poll_DeviceNotFound_ReturnsError_NotPending()
    {
        // Regression: if OpenAI no longer knows the device_auth_id (expired or
        // never authorized), the poll must surface an error so the dashboard
        // stops spinning — NOT keep returning "pending".
        using var db = CreateDb();
        var vault = Vault();
        var tenant = Tenant.Create("Test", "acme", "{}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var endpoints = new ChatGptEndpoints(
            InferenceBaseUrl: "https://chatgpt.com/backend-api/codex",
            DeviceUsercodeUrl: "https://example.test/deviceauth/usercode",
            DeviceTokenUrl: "https://example.test/deviceauth/token");
        var cfg = OAuthProviderConfig.Create("chatgpt", "ChatGPT (Codex) OAuth", OAuthGrant.DeviceCode,
            "https://auth.openai.com/oauth/token", "app_testclient",
            authorizationEndpoint: "https://auth.openai.com/oauth/authorize",
            extraAuthParams: System.Text.Json.JsonSerializer.Serialize(endpoints));
        db.OAuthProviderConfigs.Add(cfg);
        var provider = AiProvider.Create("ChatGPT Account 1", "chatgpt-acc1", 5);
        provider.AssignTenant(tenant.Id);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync();
        provider.SetAuthMethod(AuthMethod.OAuth, cfg.Id);
        await db.SaveChangesAsync();

        // Start so a pending flow exists, then make the token endpoint 404.
        var usercodeJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            device_auth_id = "device_abc",
            user_code = "1ABC-2DEF",
            verification_uri = "https://auth.openai.com/codex/device"
        });
        var svc = MakeService(db, vault, tenant.Id, new StubHandler(usercodeJson));
        var start = await svc.StartAsync("chatgpt-acc1", "https://gateway.test");
        Assert.True(start.IsDeviceCode);

        var notFoundJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            error = new { message = "Resource not found", type = "invalid_request_error", code = "deviceauth_not_found" }
        });
        var notFoundSvc = MakeService(db, vault, tenant.Id, new NotFoundHandler(notFoundJson));
        var status = await notFoundSvc.GetChatGptDeviceStatusAsync("chatgpt-acc1");

        Assert.NotNull(status);
        Assert.Equal("error", status.Status);
        Assert.Contains("not found", status.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Poll_DeviceCredentialPersistenceFailure_UsesDurableCredentialPath()
    {
        var databaseName = Guid.NewGuid().ToString();
        using var db = CreateDb(databaseName);
        var vault = Vault();
        var tenant = Tenant.Create("Test", "acme", "{}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var endpoints = new ChatGptEndpoints(
            InferenceBaseUrl: "https://chatgpt.com/backend-api/codex",
            DeviceUsercodeUrl: "https://example.test/deviceauth/usercode",
            DeviceTokenUrl: "https://example.test/deviceauth/token");
        var cfg = OAuthProviderConfig.Create("chatgpt", "ChatGPT (Codex)", OAuthGrant.DeviceCode,
            "https://auth.openai.com/oauth/token", "app_testclient",
            authorizationEndpoint: "https://auth.openai.com/oauth/authorize",
            extraAuthParams: JsonSerializer.Serialize(endpoints));
        db.OAuthProviderConfigs.Add(cfg);
        var provider = AiProvider.Create("ChatGPT Account 1", "chatgpt-acc1", 5);
        provider.AssignTenant(tenant.Id);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync();
        provider.SetAuthMethod(AuthMethod.OAuth, cfg.Id);
        await db.SaveChangesAsync();

        var usercodeJson = JsonSerializer.Serialize(new
        {
            device_auth_id = "device_abc",
            user_code = "1ABC-2DEF",
            verification_uri = "https://auth.openai.com/codex/device"
        });
        var authorizedJson = JsonSerializer.Serialize(new
        {
            authorization_code = "one-time-device-code",
            code_verifier = "device-verifier"
        });
        var handler = new SequenceHandler(usercodeJson, authorizedJson);
        var failingPending = new FailingDeletePendingFlowRepository(new OAuthPendingFlowRepository(db), failPersistDeviceCredentials: true);
        var firstService = new OAuthFlowService(
            new ProviderOAuthTokenRepository(db),
            new OAuthProviderConfigRepository(db),
            failingPending,
            new AiProviderRepository(db),
            vault,
            new StaticTenantProvider(tenant.Id),
            MakeFactory(handler),
            NullLogger<OAuthFlowService>.Instance);

        var start = await firstService.StartAsync("chatgpt-acc1", "https://gateway.test");
        var firstPoll = await firstService.PollChatGptDeviceAsync(start.State);
        Assert.True(firstPoll.Success);
        Assert.Equal("authorized", firstPoll.Status);
        Assert.Equal(0, failingPending.UpdateCalls);
        Assert.Equal(2, failingPending.PersistDeviceCredentialsCalls);

        using var retryDb = CreateDb(databaseName);
        var persisted = await retryDb.OAuthPendingFlows.SingleAsync(f => f.State == start.State);
        Assert.Equal("one-time-device-code", persisted.DecryptAuthorizationCode(vault));
        Assert.Equal("device-verifier", persisted.DecryptCodeVerifier(vault));

        var retry = await MakeService(retryDb, vault, tenant.Id, handler)
            .PollChatGptDeviceAsync(start.State);

        Assert.True(retry.Success);
        Assert.Equal("authorized", retry.Status);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task DeviceCompletion_IncompleteSuccess_PersistsTerminalFailureWithoutReplay()
    {
        using var db = CreateDb();
        var vault = Vault();
        var tenant = Tenant.Create("Test", "acme", "{}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var endpoints = new ChatGptEndpoints(
            InferenceBaseUrl: "https://chatgpt.com/backend-api/codex",
            DeviceUsercodeUrl: "https://example.test/deviceauth/usercode",
            DeviceTokenUrl: "https://example.test/deviceauth/token");
        var cfg = OAuthProviderConfig.Create("chatgpt", "ChatGPT (Codex)", OAuthGrant.DeviceCode,
            "https://auth.openai.com/oauth/token", "app_testclient",
            authorizationEndpoint: "https://auth.openai.com/oauth/authorize",
            extraAuthParams: JsonSerializer.Serialize(endpoints));
        db.OAuthProviderConfigs.Add(cfg);
        var provider = AiProvider.Create("ChatGPT Account 1", "chatgpt-acc1", 5);
        provider.AssignTenant(tenant.Id);
        provider.SetAuthMethod(AuthMethod.OAuth, cfg.Id);
        db.AiProviders.Add(provider);
        var flow = OAuthPendingFlow.Create(
            tenant.Id,
            "chatgpt",
            "device-terminal-failure-state",
            "device-verifier",
            "https://auth.openai.com/deviceauth/callback",
            vault,
            TimeSpan.FromMinutes(10),
            aiProviderCode: provider.Code,
            deviceAuthId: "device-id",
            deviceUserCode: "device-code");
        flow.SetDeviceCredentials("one-time-device-code", "device-verifier", vault, allowClaimedFlow: true);
        db.OAuthPendingFlows.Add(flow);
        await db.SaveChangesAsync();

        var handler = new StubHandler("{\"token_type\":\"Bearer\"}");
        var service = MakeService(db, vault, tenant.Id, handler);

        var first = await service.CompleteChatGptDeviceAsync(flow.State);
        Assert.Equal(OAuthTokenStatus.Error, first.Status);
        Assert.Contains("restart", first.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
        var stored = await db.OAuthPendingFlows.SingleAsync(f => f.State == flow.State);
        Assert.True(stored.HasCompletionFailure);
        Assert.Null(stored.CompletionClaimedAt);
        Assert.Equal(1, handler.Calls);

        var second = await service.CompleteChatGptDeviceAsync(flow.State);
        Assert.Equal(OAuthTokenStatus.Error, second.Status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DeviceCompletion_TokenRowFailure_RecoversWithoutReexchange()
    {
        var databaseName = Guid.NewGuid().ToString();
        using var db = CreateDb(databaseName);
        var vault = Vault();
        var tenant = Tenant.Create("Test", "acme", "{}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var endpoints = new ChatGptEndpoints(
            InferenceBaseUrl: "https://chatgpt.com/backend-api/codex",
            DeviceUsercodeUrl: "https://example.test/deviceauth/usercode",
            DeviceTokenUrl: "https://example.test/deviceauth/token");
        var cfg = OAuthProviderConfig.Create("chatgpt", "ChatGPT (Codex)", OAuthGrant.DeviceCode,
            "https://auth.openai.com/oauth/token", "app_testclient",
            authorizationEndpoint: "https://auth.openai.com/oauth/authorize",
            extraAuthParams: JsonSerializer.Serialize(endpoints));
        db.OAuthProviderConfigs.Add(cfg);
        var provider = AiProvider.Create("ChatGPT Account 1", "chatgpt-acc1", 5);
        provider.AssignTenant(tenant.Id);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync();
        provider.SetAuthMethod(AuthMethod.OAuth, cfg.Id);
        await db.SaveChangesAsync();

        var handler = new SequenceHandler(
            JsonSerializer.Serialize(new
            {
                device_auth_id = "device_abc",
                user_code = "1ABC-2DEF",
                verification_uri = "https://auth.openai.com/codex/device"
            }),
            JsonSerializer.Serialize(new
            {
                authorization_code = "one-time-device-code",
                code_verifier = "device-verifier"
            }),
            JsonSerializer.Serialize(new
            {
                access_token = "device.access.token",
                refresh_token = "device.refresh.token",
                token_type = "Bearer",
                expires_in = 3600
            }));
        var failingTokens = new FailingTokenRepository(new ProviderOAuthTokenRepository(db), failAdd: true);
        var service = new OAuthFlowService(
            failingTokens,
            new OAuthProviderConfigRepository(db),
            new OAuthPendingFlowRepository(db),
            new AiProviderRepository(db),
            vault,
            new StaticTenantProvider(tenant.Id),
            MakeFactory(handler),
            NullLogger<OAuthFlowService>.Instance);

        var start = await service.StartAsync("chatgpt-acc1", "https://gateway.test");
        var poll = await service.PollChatGptDeviceAsync(start.State);
        Assert.True(poll.Success);

        var firstCompletion = await service.CompleteChatGptDeviceAsync(start.State);
        Assert.Equal(OAuthTokenStatus.Error, firstCompletion.Status);
        Assert.Contains("pending", firstCompletion.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, handler.Calls);

        using var retryDb = CreateDb(databaseName);
        var retry = MakeService(retryDb, vault, tenant.Id, handler);
        var secondCompletion = await retry.CompleteChatGptDeviceAsync(start.State);

        Assert.Equal(OAuthTokenStatus.Connected, secondCompletion.Status);
        Assert.Equal(3, handler.Calls);
        Assert.Null(await retryDb.OAuthPendingFlows.SingleOrDefaultAsync(f => f.State == start.State));
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public int Calls { get; private set; }

        public SequenceHandler(params string[] responses) => _responses = new Queue<string>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var body = _responses.Count == 0 ? "{}" : _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FailingTokenRepository : IProviderOAuthTokenRepository
    {
        private readonly IProviderOAuthTokenRepository _inner;
        private int _failAdd;
        private int _failUpdate;

        public FailingTokenRepository(
            IProviderOAuthTokenRepository inner,
            bool failAdd = false,
            bool failUpdate = false)
        {
            _inner = inner;
            _failAdd = failAdd ? 1 : 0;
            _failUpdate = failUpdate ? 1 : 0;
        }

        public Task<ProviderOAuthToken?> GetByProviderAsync(Guid providerId, Guid tenantId, CancellationToken ct = default)
            => _inner.GetByProviderAsync(providerId, tenantId, ct);

        public Task<IReadOnlyList<ProviderOAuthToken>> GetConnectedNeedingRefreshAsync(
            TimeSpan slack, CancellationToken ct = default)
            => _inner.GetConnectedNeedingRefreshAsync(slack, ct);

        public Task AddAsync(ProviderOAuthToken token, CancellationToken ct = default)
            => Interlocked.Exchange(ref _failAdd, 0) == 1
                ? Task.FromException(new InvalidOperationException("simulated token-row add failure"))
                : _inner.AddAsync(token, ct);

        public Task UpdateAsync(ProviderOAuthToken token, CancellationToken ct = default)
            => Interlocked.Exchange(ref _failUpdate, 0) == 1
                ? Task.FromException(new InvalidOperationException("simulated token-row update failure"))
                : _inner.UpdateAsync(token, ct);
    }

    private sealed class FailingDeletePendingFlowRepository : IOAuthPendingFlowRepository
    {
        private readonly IOAuthPendingFlowRepository _inner;
        private int _failDelete = 1;
        private int _failUpdate;
        private int _failPersistDeviceCredentials;
        public int UpdateCalls { get; private set; }
        public int PersistDeviceCredentialsCalls { get; private set; }

        public FailingDeletePendingFlowRepository(
            IOAuthPendingFlowRepository inner,
            bool failUpdate = false,
            bool failPersistDeviceCredentials = false)
        {
            _inner = inner;
            _failUpdate = failUpdate ? 1 : 0;
            _failPersistDeviceCredentials = failPersistDeviceCredentials ? 1 : 0;
        }

        public Task<OAuthPendingFlow?> GetByStateAsync(string state, Guid? tenantId, CancellationToken ct = default)
            => _inner.GetByStateAsync(state, tenantId, ct);

        public Task<OAuthPendingFlow?> GetActiveByProviderCodeAsync(string aiProviderCode, Guid tenantId, CancellationToken ct = default)
            => _inner.GetActiveByProviderCodeAsync(aiProviderCode, tenantId, ct);

        public Task DeletePendingForAccountAsync(string aiProviderCode, Guid tenantId, CancellationToken ct = default)
            => _inner.DeletePendingForAccountAsync(aiProviderCode, tenantId, ct);

        public Task AddAsync(OAuthPendingFlow flow, CancellationToken ct = default)
            => _inner.AddAsync(flow, ct);

        public Task UpdateAsync(OAuthPendingFlow flow, CancellationToken ct = default)
        {
            UpdateCalls++;
            return Interlocked.Exchange(ref _failUpdate, 0) == 1
                ? Task.FromException(new InvalidOperationException("simulated pending-flow completion persistence failure"))
                : _inner.UpdateAsync(flow, ct);
        }

        public Task<bool> PersistDeviceCredentialsAsync(OAuthPendingFlow flow, Guid tenantId, DateTimeOffset claimedAt, CancellationToken ct = default)
        {
            PersistDeviceCredentialsCalls++;
            return Interlocked.Exchange(ref _failPersistDeviceCredentials, 0) == 1
                ? Task.FromException<bool>(new InvalidOperationException("simulated device credential persistence failure"))
                : _inner.PersistDeviceCredentialsAsync(flow, tenantId, claimedAt, ct);
        }

        public Task<bool> PersistCompletionFailureAsync(OAuthPendingFlow flow, Guid tenantId, DateTimeOffset claimAt, bool deviceCompletionClaim, CancellationToken ct = default)
            => _inner.PersistCompletionFailureAsync(flow, tenantId, claimAt, deviceCompletionClaim, ct);

        public Task<bool> TryClaimAsync(Guid id, Guid tenantId, DateTimeOffset claimedAt, CancellationToken ct = default)
            => _inner.TryClaimAsync(id, tenantId, claimedAt, ct);

        public Task<bool> TryReleaseClaimAsync(Guid id, Guid tenantId, DateTimeOffset claimedAt, CancellationToken ct = default)
            => _inner.TryReleaseClaimAsync(id, tenantId, claimedAt, ct);

        public Task<bool> TryClaimCompletionAsync(Guid id, Guid tenantId, DateTimeOffset claimedAt, CancellationToken ct = default)
            => _inner.TryClaimCompletionAsync(id, tenantId, claimedAt, ct);

        public Task<bool> TryReleaseCompletionClaimAsync(Guid id, Guid tenantId, DateTimeOffset claimedAt, CancellationToken ct = default)
            => _inner.TryReleaseCompletionClaimAsync(id, tenantId, claimedAt, ct);

        public Task DeleteAsync(Guid id, Guid tenantId, CancellationToken ct = default)
            => Interlocked.Exchange(ref _failDelete, 0) == 1
                ? Task.FromException(new InvalidOperationException("simulated pending-flow persistence failure"))
                : _inner.DeleteAsync(id, tenantId, ct);
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        private readonly string _json;
        public NotFoundHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class StaticTenantProvider : ITenantProvider
    {
        public StaticTenantProvider(Guid id) => TenantId = id;
        public Guid? TenantId { get; }
    }

    private sealed class NullTenantProvider : ITenantProvider
    {
        public Guid? TenantId => null;
    }
}

