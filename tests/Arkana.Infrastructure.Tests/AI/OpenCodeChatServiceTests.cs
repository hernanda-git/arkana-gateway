using System.Net;
using System.Text.Json;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.AI;
using Arkana.Infrastructure.Security;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.AI;

public sealed class OpenCodeChatServiceTests
{
    private const string ExpectedModel = "opencode-v1";
    private static readonly Guid TestTenantId = Guid.NewGuid();

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
        id = "chatcmpl-opencode-1",
        model = ExpectedModel,
        choices = new[]
        {
            new
            {
                index = 0,
                message = new
                {
                    role = "assistant",
                    content = "Hello from OpenCode!"
                }
            }
        },
        usage = new
        {
            prompt_tokens = 5,
            completion_tokens = 15,
            total_tokens = 20
        }
    });

    private static readonly string ToolCallJson = JsonSerializer.Serialize(new
    {
        id = "chatcmpl-opencode-2",
        model = ExpectedModel,
        choices = new[]
        {
            new
            {
                index = 0,
                message = new
                {
                    role = "assistant",
                    content = (string?)null,
                    tool_calls = new[]
                    {
                        new
                        {
                            id = "call_opencode_001",
                            type = "function",
                            function = new
                            {
                                name = "search_documents",
                                arguments = """{"query":"quarterly report"}"""
                            }
                        }
                    }
                }
            }
        },
        usage = new
        {
            prompt_tokens = 8,
            completion_tokens = 25,
            total_tokens = 33
        }
    });

    private static OpenCodeChatService CreateService(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var mockHandler = new MockHttpMessageHandler(handler);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("http://localhost:5000")
        };

        // SECURITY: provider API keys are sealed in the vault before being stored.
        // The mock provider returns an AiProvider with a sealed ApiKey, and the
        // service must decrypt it before sending the upstream request.
        var vault = new EnvelopeCredentialVault(TestMasterKey);

        // Mock provider catalog returning OpenCode with an API key
        var mockCatalog = Substitute.For<IProviderCatalog>();
        mockCatalog.GetByCodeAsync("opencode", TestTenantId, Arg.Any<CancellationToken>())
            .Returns(AiProvider.Create("OpenCode", "opencode", 0,
                apiKeyPlaintext: "opencode-key",
                baseUrl: "http://localhost:5000",
                vault: vault));

        return new OpenCodeChatService(httpClient, mockCatalog, vault, new Arkana.Infrastructure.AI.Dialects.OpenAiDialectTranslator());
    }

    // 32-byte test key — must match the key used to seal the mock provider.
    private static readonly byte[] TestMasterKey = new byte[]
    {
        0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10,
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00
    };

    // ──────────────────────────────────────────────
    //   Test 1: Successful response
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_Success_ReturnsChatResult()
    {
        // Arrange
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json")
        });

        // Act
        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Be("Hello from OpenCode!");
        result.Model.Should().Be(ExpectedModel);
        result.InputTokens.Should().Be(5);
        result.OutputTokens.Should().Be(15);
        result.ErrorMessage.Should().BeNull();
        result.ToolCalls.Should().BeNull();
    }

    // ──────────────────────────────────────────────
    //   Test 2: Tool calls in response
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_WithToolCalls_ParsesToolCalls()
    {
        // Arrange
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ToolCallJson, System.Text.Encoding.UTF8, "application/json")
        });

        // Act
        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.ToolCalls.Should().NotBeNull();
        result.ToolCalls.Should().HaveCount(1);

        var toolCall = result.ToolCalls![0];
        toolCall.Id.Should().Be("call_opencode_001");
        toolCall.Type.Should().Be("function");
        toolCall.FunctionName.Should().Be("search_documents");
        toolCall.FunctionArguments.Should().Be("""{"query":"quarterly report"}""");

        result.InputTokens.Should().Be(8);
        result.OutputTokens.Should().Be(25);
    }

    // ──────────────────────────────────────────────
    //   Test 3: HTTP error (500)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_HttpError_ReturnsErrorResult()
    {
        // Arrange
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Internal Server Error")
        });

        // Act
        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage.Should().Contain("500");
    }

    // ──────────────────────────────────────────────
    //   Test 4: Empty/null JSON response
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_EmptyResponse_ThrowsAndReturnsError()
    {
        // Arrange — return literal JSON "null" to trigger null-deserialization error
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json")
        });

        // Act
        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        // Assert — 14b-2: the dialect translator handles the null body; the
        // connector prefixes "OpenCode:" to identify which connector observed
        // the failure. Assert the connector prefix is present.
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage.Should().Contain("OpenCode");
        result.ErrorMessage.Should().Contain("empty response");
    }
}
