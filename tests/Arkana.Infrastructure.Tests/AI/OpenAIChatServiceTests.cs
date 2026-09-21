using System.Net;
using System.Text.Json;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.ValueObjects;
using Arkana.Infrastructure.AI;
using Arkana.Infrastructure.Security;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.AI;

public sealed class OpenAIChatServiceTests
{
    private const string ExpectedModel = "gpt-4o-mini";
    private static readonly Guid TestTenantId = Guid.NewGuid();

    private static readonly ChatRequest DefaultRequest = new()
    {
        Model = "gpt-4o-mini",
        TenantId = TestTenantId,
        Messages =
        [
            new ChatMessage { Role = "user", Content = "Hello" }
        ],
    };

    private static readonly string SuccessJson = JsonSerializer.Serialize(new
    {
        id = "chatcmpl-123",
        model = ExpectedModel,
        choices = new[]
        {
            new
            {
                index = 0,
                message = new
                {
                    role = "assistant",
                    content = "Hello! How can I help you?"
                }
            }
        },
        usage = new
        {
            prompt_tokens = 10,
            completion_tokens = 20,
            total_tokens = 30
        }
    });

    private static readonly string ToolCallJson = JsonSerializer.Serialize(new
    {
        id = "chatcmpl-456",
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
                            id = "call_abc123",
                            type = "function",
                            function = new
                            {
                                name = "get_weather",
                                arguments = """{"location":"San Francisco"}"""
                            }
                        }
                    }
                }
            }
        },
        usage = new
        {
            prompt_tokens = 20,
            completion_tokens = 30,
            total_tokens = 50
        }
    });

    private static OpenAIChatService CreateService(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var mockHandler = new MockHttpMessageHandler(handler);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.openai.com")
        };

        // SECURITY: sealed API key in the mock provider, decrypted at request time.
        var vault = new EnvelopeCredentialVault(TestMasterKey);

        // Mock provider catalog returning OpenAI with an API key
        var mockCatalog = Substitute.For<IProviderCatalog>();
        mockCatalog.GetByCodeAsync("openai", TestTenantId, Arg.Any<CancellationToken>())
            .Returns(AiProvider.Create("OpenAI", "openai", 1,
                apiKeyPlaintext: "sk-test-key",
                vault: vault));

        return new OpenAIChatService(httpClient, mockCatalog, vault, new Arkana.Infrastructure.AI.Dialects.OpenAiDialectTranslator());
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
        result.Content.Should().Be("Hello! How can I help you?");
        result.Model.Should().Be(ExpectedModel);
        result.InputTokens.Should().Be(10);
        result.OutputTokens.Should().Be(20);
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
        toolCall.Id.Should().Be("call_abc123");
        toolCall.Type.Should().Be("function");
        toolCall.FunctionName.Should().Be("get_weather");
        toolCall.FunctionArguments.Should().Be("""{"location":"San Francisco"}""");

        result.InputTokens.Should().Be(20);
        result.OutputTokens.Should().Be(30);
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
            Content = new StringContent("Server Error")
        });

        // Act
        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage.Should().Contain("500");
    }

    // ──────────────────────────────────────────────
    //   Test 4: Null JSON response
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_NullResponse_ThrowsAndReturnsError()
    {
        // Arrange — return literal JSON "null" which deserializes to null
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json")
        });

        // Act
        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        // Assert — 14b-2: the dialect translator now reports the empty
        // upstream; the connector is just the transport and adds no prefix
        // when the translator's message already identifies the dialect.
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage.Should().Contain("empty response");
    }
}
