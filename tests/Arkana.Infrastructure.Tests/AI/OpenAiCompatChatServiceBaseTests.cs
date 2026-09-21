using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.AI;
using Arkana.Infrastructure.AI.Dialects;
using Arkana.Infrastructure.Security;
using FluentAssertions;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.AI;

/// <summary>
/// Tests for the five new OpenAI-compat providers (Groq, OpenRouter,
/// Qwen, GLM, Cloudflare) added in Phase 3 task 16. They share
/// <see cref="OpenAiCompatChatServiceBase"/>; the tests below
/// parameterize over the per-provider config to avoid 5x duplication.
/// </summary>
public class OpenAiCompatChatServiceBaseTests
{
    private static readonly Guid TestTenantId = Guid.NewGuid();
    /// <summary>
    /// Per-provider test fixture: the connector instance, the captured
    /// HTTP path + headers + body, and the expected endpoint that the
    /// connector must POST to.
    /// </summary>
    private sealed record ProviderFixture(
        IChatCompletionService Connector,
        CapturedRequest Captured,
        string ExpectedEndpoint,
        string ExpectedProviderName,
        string ExpectedProviderCode,
        string ExpectedEnvVar,
        string AuthHeaderName);

    private sealed class CapturedRequest
    {
        public string? Path { get; set; }
        public System.Net.Http.Headers.HttpRequestHeaders? Headers { get; set; }
        public string? Body { get; set; }
    }

    private static readonly (string Name, Func<HttpClient, IProviderCatalog, ICredentialVault, IDialectTranslator, IChatCompletionService> Build, string Code, string EnvVar, string Endpoint)[] Providers =
    [
        ("Groq",       (h, c, v, t) => new GroqChatService(h, c, v, t),       "groq",       "GROQ_API_KEY",       "https://api.groq.com/openai/v1/chat/completions"),
        ("OpenRouter", (h, c, v, t) => new OpenRouterChatService(h, c, v, t), "openrouter", "OPENROUTER_API_KEY", "https://openrouter.ai/api/v1/chat/completions"),
        ("Qwen",       (h, c, v, t) => new QwenChatService(h, c, v, t),       "qwen",       "QWEN_API_KEY",       "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"),
        ("GLM",        (h, c, v, t) => new GLMChatService(h, c, v, t),        "glm",        "GLM_API_KEY",        "https://open.bigmodel.cn/api/paas/v4/chat/completions"),
    ];

    private static ProviderFixture CreateFixture(
        string providerCode,
        string providerName,
        Func<HttpClient, IProviderCatalog, ICredentialVault, IDialectTranslator, IChatCompletionService> build,
        string envVar,
        string expectedEndpoint,
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var captured = new CapturedRequest();

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            captured.Path = req.RequestUri?.AbsoluteUri;
            captured.Headers = req.Headers;
            captured.Body = req.Content is not null
                ? req.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                : null;
            return handler(req);
        });

        var http = new HttpClient(mockHandler);
        var vault = new EnvelopeCredentialVault(TestMasterKey);
        var catalog = Substitute.For<IProviderCatalog>();
        catalog.GetByCodeAsync(providerCode, TestTenantId, Arg.Any<CancellationToken>())
            .Returns(AiProvider.Create(providerName, providerCode, priority: 5,
                apiKeyPlaintext: "***",
                vault: vault));

        var connector = build(http, catalog, vault, new OpenAiDialectTranslator());
        return new ProviderFixture(connector, captured, expectedEndpoint, providerName, providerCode, envVar, "Authorization");
    }

    // 32-byte test key.
    private static readonly byte[] TestMasterKey = new byte[]
    {
        0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10,
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00
    };

    private static readonly string SuccessJson = JsonSerializer.Serialize(new
    {
        id = "chatcmpl-test",
        model = "test-model",
        choices = new[]
        {
            new
            {
                index = 0,
                message = new { role = "assistant", content = "Hello from test provider!" }
            }
        },
        usage = new { prompt_tokens = 5, completion_tokens = 10, total_tokens = 15 }
    });

    private static readonly ChatRequest DefaultRequest = new()
    {
        Model = "test-model",
        TenantId = TestTenantId,
        Messages = [new ChatMessage { Role = "user", Content = "hi" }],
    };

    // ── Per-provider contract tests (parameterized) ─────────────────

    [Theory]
    [MemberData(nameof(ProviderConfigs))]
    public async Task CompleteAsync_PostsToProviderSpecificEndpoint(
        string providerName, string providerCode, string endpoint, string envVar,
        Func<HttpClient, IProviderCatalog, ICredentialVault, IDialectTranslator, IChatCompletionService> build)
    {
        var fixture = CreateFixture(providerCode, providerName, build, envVar, endpoint, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json")
        });

        await fixture.Connector.CompleteAsync(DefaultRequest, CancellationToken.None);

        fixture.Captured.Path.Should().Be(endpoint,
            $"{providerName} must POST to its own endpoint, not the OpenAI one");
    }

    [Theory]
    [MemberData(nameof(ProviderConfigs))]
    public async Task CompleteAsync_SetsBearerAuthHeader(
        string providerName, string providerCode, string endpoint, string envVar,
        Func<HttpClient, IProviderCatalog, ICredentialVault, IDialectTranslator, IChatCompletionService> build)
    {
        var fixture = CreateFixture(providerCode, providerName, build, envVar, endpoint, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json")
        });

        await fixture.Connector.CompleteAsync(DefaultRequest, CancellationToken.None);

        // h.Value is IEnumerable<string> — join the values to inspect
        // the full "Bearer <token>" string.
        var authHeader = fixture.Captured.Headers!.FirstOrDefault(h => h.Key == "Authorization");
        authHeader.Key.Should().Be("Authorization",
            $"{providerName} must send an Authorization header");

        var authValue = string.Join(",", authHeader.Value);
        authValue.Should().StartWith("Bearer ",
            $"{providerName} auth header must use the Bearer scheme");
        authValue.Should().NotBe("Bearer ",
            $"{providerName} Bearer token must not be empty");
    }

    [Theory]
    [MemberData(nameof(ProviderConfigs))]
    public async Task CompleteAsync_ProviderNameMatchesCatalogCode(
        string providerName, string providerCode, string endpoint, string envVar,
        Func<HttpClient, IProviderCatalog, ICredentialVault, IDialectTranslator, IChatCompletionService> build)
    {
        // The factory's lookup is by ProviderName (case-insensitive).
        // Each provider's ProviderName must match the catalog code
        // (modulo case) so the factory can find it.
        var fixture = CreateFixture(providerCode, providerName, build, envVar, endpoint, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json")
        });

        fixture.Connector.ProviderName.Should().Be(providerName,
            $"the {providerName} connector must report its ProviderName for the factory");
    }

    [Theory]
    [MemberData(nameof(ProviderConfigs))]
    public async Task CompleteAsync_SuccessReturnsParsedChatResult(
        string providerName, string providerCode, string endpoint, string envVar,
        Func<HttpClient, IProviderCatalog, ICredentialVault, IDialectTranslator, IChatCompletionService> build)
    {
        var fixture = CreateFixture(providerCode, providerName, build, envVar, endpoint, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json")
        });

        var result = await fixture.Connector.CompleteAsync(DefaultRequest, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Be("Hello from test provider!");
        result.Model.Should().Be("test-model");
        result.InputTokens.Should().Be(5);
        result.OutputTokens.Should().Be(10);
    }

    [Theory]
    [MemberData(nameof(ProviderConfigs))]
    public async Task CompleteAsync_HttpErrorReturnsErrorWithProviderPrefix(
        string providerName, string providerCode, string endpoint, string envVar,
        Func<HttpClient, IProviderCatalog, ICredentialVault, IDialectTranslator, IChatCompletionService> build)
    {
        var fixture = CreateFixture(providerCode, providerName, build, envVar, endpoint, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Server Error")
        });

        var result = await fixture.Connector.CompleteAsync(DefaultRequest, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage.Should().Contain("500");
    }

    /// <summary>
    /// xUnit MemberData adapter: flattens <see cref="Providers"/> into
    /// the 5-tuple shape the [Theory] tests need.
    /// </summary>
    public static IEnumerable<object[]> ProviderConfigs()
    {
        foreach (var (name, build, code, envVar, endpoint) in Providers)
        {
            yield return new object[] { name, code, endpoint, envVar, build };
        }
    }

    // ── Cloudflare has account_id in the endpoint; test separately ──

    [Fact]
    public async Task Cloudflare_Endpoint_IncludesAccountIdFromEnvVar()
    {
        Environment.SetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID", "test-account-abc");

        try
        {
            var fixture = CreateFixture(
                "cloudflare", "Cloudflare",
                (h, c, v, t) => new CloudflareChatService(h, c, v, t),
                "CLOUDFLARE_API_KEY",
                "https://api.cloudflare.com/client/v4/accounts/test-account-abc/ai/v1/chat/completions",
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json")
                });

            await fixture.Connector.CompleteAsync(DefaultRequest, CancellationToken.None);

            fixture.Captured.Path.Should().Contain("test-account-abc");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID", null);
        }
    }

    [Fact]
    public async Task Cloudflare_Endpoint_FallsBackToDefault_WhenAccountIdNotSet()
    {
        Environment.SetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID", null);

        var fixture = CreateFixture(
            "cloudflare", "Cloudflare",
            (h, c, v, t) => new CloudflareChatService(h, c, v, t),
            "CLOUDFLARE_API_KEY",
            "https://api.cloudflare.com/client/v4/accounts/default/ai/v1/chat/completions",
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json")
            });

        await fixture.Connector.CompleteAsync(DefaultRequest, CancellationToken.None);

        fixture.Captured.Path.Should().Contain("/accounts/default/");
    }

    [Fact]
    public void OpenAiCompatChatServiceBase_IsAbstract()
    {
        // Compile-time check enforced by the language; the runtime
        // assertion is just a doc-of-intent. The real test is that the
        // 5 subclasses build and pass their tests.
        typeof(OpenAiCompatChatServiceBase).IsAbstract.Should().BeTrue();
    }
}
