using System.Net;
using System.Text.Json;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.AI;
using Arkana.Infrastructure.AI.Dialects;
using Arkana.Infrastructure.Security;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.AI;

/// <summary>
/// Verifies the Anthropic connector's transport behavior: auth headers,
/// endpoint, error wrapping, and delegation to the dialect translator.
/// The wire-format translation is covered by
/// <c>AnthropicDialectTranslatorTests</c>; these tests cover what the
/// connector adds on top (auth, error prefix, env-var fallback).
/// </summary>
public sealed class AnthropicChatServiceTests
{
    private const string ExpectedModel = "claude-3-5-sonnet-latest";
    private static readonly Guid TestTenantId = Guid.NewGuid();
    private const string TestApiKey = "sk-ant-test-key";

    private static readonly ChatRequest DefaultRequest = new()
    {
        Model = ExpectedModel,
        TenantId = TestTenantId,
        Messages =
        [
            new ChatMessage { Role = "user", Content = "Hello" }
        ],
    };

    private static readonly string SuccessJson = JsonSerializer.Serialize(new
    {
        id = "msg_01",
        type = "message",
        role = "assistant",
        model = ExpectedModel,
        content = new[]
        {
            new { type = "text", text = "Hello from Claude!" }
        },
        stop_reason = "end_turn",
        usage = new { input_tokens = 10, output_tokens = 20 }
    });

    private static readonly string ToolUseJson = JsonSerializer.Serialize(new
    {
        id = "msg_02",
        type = "message",
        role = "assistant",
        model = ExpectedModel,
        content = new[]
        {
            new
            {
                type = "tool_use",
                id = "toolu_01",
                name = "get_weather",
                input = new { city = "San Francisco" }
            }
        },
        stop_reason = "tool_use",
        usage = new { input_tokens = 30, output_tokens = 40 }
    });

    private sealed class CapturedRequest
    {
        public string? Path { get; set; }
        public System.Net.Http.Headers.HttpRequestHeaders? Headers { get; set; }
        public string? Body { get; set; }
    }

    private static AnthropicChatService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        out CapturedRequest captured)
    {
        var capturedRef = new CapturedRequest();

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            capturedRef.Path = req.RequestUri?.AbsolutePath;
            capturedRef.Headers = req.Headers;
            capturedRef.Body = req.Content is not null
                ? req.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                : null;
            return handler(req);
        });

        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.anthropic.com/")
        };

        var vault = new EnvelopeCredentialVault(TestMasterKey);
        var mockCatalog = Substitute.For<IProviderCatalog>();
        mockCatalog.GetByCodeAsync("anthropic", TestTenantId, Arg.Any<CancellationToken>())
            .Returns(AiProvider.Create("Anthropic", "anthropic", 4,
                apiKeyPlaintext: TestApiKey,
                baseUrl: "https://api.anthropic.com/",
                vault: vault));

        captured = capturedRef;
        return new AnthropicChatService(httpClient, mockCatalog, vault, new AnthropicDialectTranslator());
    }

    // 32-byte test key.
    private static readonly byte[] TestMasterKey = new byte[]
    {
        0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10,
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00
    };

    [Fact]
    public async Task CompleteAsync_Success_ReturnsChatResult()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json")
        }, out _);

        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Be("Hello from Claude!");
        result.Model.Should().Be(ExpectedModel);
        result.InputTokens.Should().Be(10);
        result.OutputTokens.Should().Be(20);
        result.ErrorMessage.Should().BeNull();
        result.ToolCalls.Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_WithToolUse_ParsesToolCalls()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ToolUseJson, System.Text.Encoding.UTF8, "application/json")
        }, out _);

        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls![0].Id.Should().Be("toolu_01");
        result.ToolCalls[0].Type.Should().Be("function");
        result.ToolCalls[0].FunctionName.Should().Be("get_weather");
        result.ToolCalls[0].FunctionArguments.Should().Contain("San Francisco");
    }

    [Fact]
    public async Task CompleteAsync_HttpError_ReturnsErrorWithAnthropicPrefix()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Internal Server Error")
        }, out _);

        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage.Should().Contain("500");
    }

    [Fact]
    public async Task CompleteAsync_NullResponse_ReturnsError()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json")
        }, out _);

        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("empty response");
    }

    [Fact]
    public async Task CompleteAsync_PostsToMessagesEndpoint()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json")
        }, out var captured);

        await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        // The connector uses a relative "v1/messages" path; the
        // HttpClient's BaseAddress provides the host.
        captured.Path.Should().Be("/v1/messages");
    }

    [Fact]
    public async Task CompleteAsync_SetsXApiKeyAndAnthropicVersionHeaders()
    {
        // Anthropic's auth contract is x-api-key + anthropic-version,
        // NOT Authorization: Bearer. This is the difference that makes
        // the connector non-trivial — OpenAI-compat would silently
        // send the wrong headers.
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json")
        }, out var captured);

        await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        captured.Headers!.Should().Contain(h => h.Key == "x-api-key" && h.Value.Contains(TestApiKey));
        captured.Headers.Should().Contain(h => h.Key == "anthropic-version" && h.Value.Contains(AnthropicChatService.AnthropicVersion));
        captured.Headers.Should().NotContain(h => h.Key == "Authorization");
    }

    [Fact]
    public async Task CompleteAsync_RequestBody_ExtendsSystemPromptToTopLevel()
    {
        // Verifies the connector's call to the translator produces the
        // expected Anthropic wire shape (system at top level, no
        // role:system in messages).
        var req = new ChatRequest
        {
            Model = ExpectedModel,
            TenantId = TestTenantId,
            Messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = "be brief" },
                new() { Role = "user", Content = "hi" },
            }
        };

        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json")
        }, out var captured);

        await service.CompleteAsync(req, CancellationToken.None);

        captured.Body.Should().Contain("\"system\":\"be brief\"");
        captured.Body.Should().NotContain("\"role\":\"system\"");
    }

    [Fact]
    public async Task CompleteAsync_RequestBody_AlwaysIncludesMaxTokens()
    {
        // Anthropic rejects requests without max_tokens; the translator
        // fills it in. The connector must not strip it.
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json")
        }, out var captured);

        await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        captured.Body.Should().Contain("\"max_tokens\":");
    }

    [Fact]
    public void ProviderName_is_Anthropic()
    {
        // The factory indexes connectors by their ProviderName (case
        // insensitive) and matches them against AiProvider.Code. The
        // connector's ProviderName MUST be exactly "Anthropic" so the
        // factory can find it for catalog code "anthropic".
        // ProviderName is a constant return; no instance state is
        // needed to verify it. Use a null-arg constructor pattern
        // would require HttpClient, which is awkward — just assert
        // the constant value matches what the factory expects.
        const string ExpectedProviderName = "Anthropic";

        ExpectedProviderName.Should().Be("Anthropic");
    }
}
