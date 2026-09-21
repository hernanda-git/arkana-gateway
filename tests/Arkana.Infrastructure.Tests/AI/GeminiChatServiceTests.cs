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
/// Verifies the Gemini connector's transport behavior: auth headers,
/// endpoint, error wrapping, and delegation to the dialect translator.
/// The wire-format translation is covered by
/// <c>GeminiDialectTranslatorTests</c>; these tests cover what the
/// connector adds on top (x-goog-api-key header, model-in-URL path,
/// error prefix, env-var fallback).
/// </summary>
public sealed class GeminiChatServiceTests
{
    private const string ExpectedModel = "gemini-2.0-flash";
    private const string TestApiKey = "***";
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
        candidates = new[]
        {
            new
            {
                content = new
                {
                    role = "model",
                    parts = new[] { new { text = "Hello from Gemini!" } },
                },
                finishReason = "STOP",
            }
        },
        usageMetadata = new { promptTokenCount = 10, candidatesTokenCount = 20, totalTokenCount = 30 },
    });

    private static readonly string FunctionCallJson = JsonSerializer.Serialize(new
    {
        candidates = new[]
        {
            new
            {
                content = new
                {
                    role = "model",
                    parts = new object[]
                    {
                        new { text = "Let me check the weather" },
                        new
                        {
                            functionCall = new
                            {
                                name = "get_weather",
                                args = new { city = "Jakarta" },
                            },
                        },
                    },
                },
                finishReason = "STOP",
            }
        },
        usageMetadata = new { promptTokenCount = 15, candidatesTokenCount = 25 },
    });

    private sealed class CapturedRequest
    {
        public string? Path { get; set; }
        public string? Query { get; set; }
        public System.Net.Http.Headers.HttpRequestHeaders? Headers { get; set; }
        public string? Body { get; set; }
    }

    private static GeminiChatService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        out CapturedRequest captured,
        string catalogCode = "gemini",
        AiProvider? catalogProvider = null,
        IOAuthTokenResolver? oauthResolver = null)
    {
        var capturedRef = new CapturedRequest();

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            capturedRef.Path = req.RequestUri?.AbsolutePath;
            capturedRef.Query = req.RequestUri?.Query;
            capturedRef.Headers = req.Headers;
            capturedRef.Body = req.Content is not null
                ? req.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                : null;
            return handler(req);
        });

        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/"),
        };

        var vault = new EnvelopeCredentialVault(TestMasterKey);
        var mockCatalog = Substitute.For<IProviderCatalog>();
        mockCatalog.GetByCodeAsync(catalogCode, TestTenantId, Arg.Any<CancellationToken>())
            .Returns(catalogProvider ?? AiProvider.Create("Gemini", "gemini", 5,
                apiKeyPlaintext: TestApiKey,
                baseUrl: null,
                vault: vault));

        captured = capturedRef;
        return new GeminiChatService(httpClient, mockCatalog, vault, new GeminiDialectTranslator(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GeminiChatService>.Instance,
            oauthResolver);
    }

    // 32-byte test key (matches Anthropic tests).
    private static readonly byte[] TestMasterKey =
    [
        0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10,
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00,
    ];

    [Fact]
    public async Task CompleteAsync_Success_ReturnsChatResult()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json"),
        }, out _);

        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Be("Hello from Gemini!");
        result.Model.Should().Be(ExpectedModel);
        result.InputTokens.Should().Be(10);
        result.OutputTokens.Should().Be(20);
        result.ErrorMessage.Should().BeNull();
        result.ToolCalls.Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_WithFunctionCall_ParsesToolCalls()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(FunctionCallJson, System.Text.Encoding.UTF8, "application/json"),
        }, out _);

        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls![0].Type.Should().Be("function");
        result.ToolCalls[0].FunctionName.Should().Be("get_weather");
        result.ToolCalls[0].FunctionArguments.Should().Contain("Jakarta");
    }

    [Fact]
    public async Task CompleteAsync_HttpError_ReturnsErrorWithGeminiPrefix()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Internal Server Error"),
        }, out _);

        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage.Should().Contain("service is unavailable");
        result.ErrorMessage.Should().Contain("Gemini");
    }

    [Fact]
    public async Task CompleteAsync_PostsToGenerateContentEndpoint()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json"),
        }, out var captured);

        await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        // The connector passes the model in the URL path:
        // /v1beta/models/{model}:generateContent
        captured.Path.Should().Contain("models/gemini-2.0-flash:generateContent");
    }

    [Fact]
    public async Task CompleteAsync_SyntheticAccountModel_UsesBaseModelInEndpoint()
    {
        var provider = AiProvider.Create("Native Gemini account", "gemini-acc3", 5);
        provider.SetAuthMethod(AuthMethod.OAuth, Guid.NewGuid());
        var resolver = Substitute.For<IOAuthTokenResolver>();
        resolver.GetBearerTokenAsync(provider.Id, TestTenantId, Arg.Any<CancellationToken>())
            .Returns("unit-test-bearer");
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json"),
        }, out var captured, "gemini-acc3", provider, resolver);

        await service.CompleteAsync(
            DefaultRequest with
            {
                Model = "gemini-2.0-flash-acc3",
                PreferredProviderCode = "gemini-acc3",
                PreferredProviderAccountCode = "gemini-acc3"
            }, CancellationToken.None);

        captured.Path.Should().Contain("models/gemini-2.0-flash:generateContent");
        captured.Path.Should().NotContain("gemini-2.0-flash-acc3");
    }

    [Fact]
    public async Task CompleteAsync_SetsXGoogApiKeyHeader()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json"),
        }, out var captured);

        await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        // Gemini uses x-goog-api-key, NOT Authorization: Bearer.
        captured.Headers!.Should().Contain(h => h.Key == "x-goog-api-key" && h.Value.Contains(TestApiKey));
        captured.Headers.Should().NotContain(h => h.Key == "Authorization");
    }

    [Fact]
    public async Task CompleteAsync_RequestBody_ContainsContentsArray()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json"),
        }, out var captured);

        await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        captured.Body.Should().Contain("\"contents\"");
        captured.Body.Should().Contain("\"user\"");
    }

    [Fact]
    public async Task CompleteAsync_RequestBody_UsesModelRoleForAssistant()
    {
        var req = new ChatRequest
        {
            Model = ExpectedModel,
            TenantId = TestTenantId,
            Messages =
            [
                new ChatMessage { Role = "user", Content = "hello" },
                new ChatMessage { Role = "assistant", Content = "world" },
            ],
        };

        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json"),
        }, out var captured);

        await service.CompleteAsync(req, CancellationToken.None);

        // Gemini uses "model" role, not "assistant".
        captured.Body.Should().Contain("\"model\"");
        captured.Body.Should().NotContain("\"assistant\"");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication failed")]
    [InlineData(HttpStatusCode.Forbidden, "authorization failed")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit exceeded")]
    [InlineData(HttpStatusCode.InternalServerError, "service is unavailable")]
    [InlineData(HttpStatusCode.BadGateway, "service is unavailable")]
    public async Task CompleteAsync_HttpError_NeverExposesUpstreamBody(HttpStatusCode status, string expectedText)
    {
        const string sensitiveBody = "fake_access_token account_id=acct-123 Authorization: Bearer secret request={payload sensitive_field}";
        var service = CreateService(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(sensitiveBody),
        }, out _);

        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        result.ErrorMessage.Should().Contain(expectedText);
        result.ErrorMessage.Should().NotContain(sensitiveBody);
        result.ErrorMessage.Should().NotContain("fake_access_token");
        result.ErrorMessage.Should().NotContain("acct-123");
        result.ErrorMessage.Should().NotContain("Authorization");
        result.UpstreamStatus.Should().Be((int)status);
    }

    [Fact]
    public async Task CompleteAsync_MalformedJson_UsesStableMessage()
    {
        const string sensitiveBody = "{ invalid fake_access_token account_id=acct-123 }";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sensitiveBody),
        }, out _);

        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        result.ErrorMessage.Should().Be("Gemini: invalid JSON response.");
        result.ErrorMessage.Should().NotContain("fake_access_token");
        result.ErrorMessage.Should().NotContain("acct-123");
    }
    [Fact]
    public async Task CompleteAsync_Cancellation_ReturnsStableMessage()
    {
        var service = CreateService(_ => throw new OperationCanceledException(new CancellationToken(true)), out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await service.CompleteAsync(DefaultRequest, cts.Token);

        result.ErrorMessage.Should().Be("Gemini request was cancelled.");
    }

    [Fact]
    public async Task CompleteAsync_Timeout_ReturnsStableMessage()
    {
        var service = CreateService(_ => throw new OperationCanceledException(), out _);

        var result = await service.CompleteAsync(DefaultRequest, CancellationToken.None);

        result.ErrorMessage.Should().Be("Gemini upstream request timed out.");
    }


    [Fact]
    public async Task CompleteStreamingAsync_UsesSseEndpointAndOwnsProviderStream()
    {
        var sse = $"data: {SuccessJson}\n\n";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sse))),
        }, out var captured);

        await using var streamResult = await service.CompleteStreamingAsync(DefaultRequest, CancellationToken.None);

        streamResult.IsSuccess.Should().BeTrue();
        streamResult.Result.RouteKind.Should().Be("native");
        captured.Path.Should().Contain("models/gemini-2.0-flash:streamGenerateContent");
        captured.Query.Should().Be("?alt=sse");
        using var reader = new StreamReader(streamResult.Stream!);
        (await reader.ReadToEndAsync()).Should().Contain("data:");
    }

    [Fact]
    public async Task CompleteStreamingAsync_SyntheticAccountModel_UsesBaseModelInEndpoint()
    {
        var provider = AiProvider.Create("Native Gemini account", "gemini-acc3", 5);
        provider.SetAuthMethod(AuthMethod.OAuth, Guid.NewGuid());
        var resolver = Substitute.For<IOAuthTokenResolver>();
        resolver.GetBearerTokenAsync(provider.Id, TestTenantId, Arg.Any<CancellationToken>())
            .Returns("unit-test-bearer");
        var sse = $"data: {SuccessJson}\n\n";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sse))),
        }, out var captured, "gemini-acc3", provider, resolver);

        await using var streamResult = await service.CompleteStreamingAsync(
            DefaultRequest with
            {
                Model = "gemini-2.0-flash-acc3",
                PreferredProviderCode = "gemini-acc3",
                PreferredProviderAccountCode = "gemini-acc3"
            }, CancellationToken.None);

        streamResult.IsSuccess.Should().BeTrue();
        captured.Path.Should().Contain("models/gemini-2.0-flash:streamGenerateContent");
        captured.Path.Should().NotContain("gemini-2.0-flash-acc3");
    }

    [Fact]
    public void NativeStreamTranslator_MapsTextUsageAndFinishReason()
    {
        var state = new GeminiNativeStreamState();

        GeminiNativeStreamTranslator.TryTranslate(
            SuccessJson,
            ExpectedModel,
            state,
            out var translated,
            out var promptTokens,
            out var completionTokens).Should().BeTrue();

        translated.Should().Contain("Hello from Gemini!");
        translated.Should().Contain("finish_reason");
        state.SawFinish.Should().BeTrue();
        promptTokens.Should().Be(10);
        completionTokens.Should().Be(20);
    }

    [Fact]
    public void NativeStreamTranslator_MapsFunctionCallToToolDelta()
    {
        var state = new GeminiNativeStreamState();

        GeminiNativeStreamTranslator.TryTranslate(
            FunctionCallJson,
            ExpectedModel,
            state,
            out var translated,
            out _,
            out _).Should().BeTrue();

        translated.Should().Contain("tool_calls");
        translated.Should().Contain("get_weather");
        translated.Should().Contain("Jakarta");
        state.SawFinish.Should().BeTrue();
    }

    [Fact]
    public void NativeStreamTranslator_KeepsRepeatedFunctionNamesAsDistinctCalls()
    {
        const string repeatedCalls = """
            {"candidates":[{"content":{"parts":[
              {"functionCall":{"name":"get_weather","args":{"city":"Jakarta"}}},
              {"functionCall":{"name":"get_weather","args":{"city":"Bandung"}}}
            ]},"finishReason":"STOP"}]}
            """;
        var state = new GeminiNativeStreamState();

        GeminiNativeStreamTranslator.TryTranslate(
            repeatedCalls, ExpectedModel, state, out var translated, out _, out _)
            .Should().BeTrue();

        using var document = JsonDocument.Parse(translated!);
        var toolCalls = document.RootElement
            .GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls");
        toolCalls.GetArrayLength().Should().Be(2);
        toolCalls[0].GetProperty("id").GetString()
            .Should().NotBe(toolCalls[1].GetProperty("id").GetString());
        toolCalls[0].GetProperty("index").GetInt32().Should().Be(0);
        toolCalls[1].GetProperty("index").GetInt32().Should().Be(1);
    }

    [Fact]
    public void NativeStreamTranslator_RejectsUnknownFinishReason()
    {
        const string unknownFinish = """
            {"candidates":[{"content":{"parts":[{"text":"unsafe"}]},"finishReason":"NEW_REASON"}]}
            """;
        var state = new GeminiNativeStreamState();

        GeminiNativeStreamTranslator.TryTranslate(
            unknownFinish, ExpectedModel, state, out _, out _, out _)
            .Should().BeFalse();
        state.SawFinish.Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_OAuthProvider_UsesBearerWithoutApiKeyHeader()
    {
        var vault = new EnvelopeCredentialVault(TestMasterKey);
        var provider = AiProvider.Create("Native Gemini account", "gemini-acc1", 5);
        provider.SetAuthMethod(AuthMethod.OAuth, Guid.NewGuid());
        var resolver = Substitute.For<IOAuthTokenResolver>();
        resolver.GetBearerTokenAsync(provider.Id, TestTenantId, Arg.Any<CancellationToken>())
            .Returns("unit-test-bearer");
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json"),
        }, out var captured, "gemini-acc1", provider, resolver);

        var result = await service.CompleteAsync(
            DefaultRequest with
            {
                PreferredProviderCode = "gemini-acc1",
                PreferredProviderAccountCode = "gemini-acc1"
            }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.ResolvedProviderAccountCode.Should().Be("gemini-acc1");
        captured.Headers.Should().Contain(h => h.Key == "Authorization");
        captured.Headers.Should().NotContain(h => h.Key == "x-goog-api-key");
    }

    [Fact]
    public async Task CompleteAsync_OAuthProviderWithoutToken_FailsBeforeOutboundRequest()
    {
        var provider = AiProvider.Create("Native Gemini account", "gemini-acc1", 5);
        provider.SetAuthMethod(AuthMethod.OAuth, Guid.NewGuid());
        var resolver = Substitute.For<IOAuthTokenResolver>();
        resolver.GetBearerTokenAsync(provider.Id, TestTenantId, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var service = CreateService(_ => throw new InvalidOperationException("outbound request must not occur"),
            out var captured, "gemini-acc1", provider, resolver);

        var result = await service.CompleteAsync(
            DefaultRequest with { PreferredProviderCode = "gemini-acc1" }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("reconnect");
        captured.Path.Should().BeNull();
        await resolver.Received(1).GetBearerTokenAsync(provider.Id, TestTenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ProviderName_is_Gemini()
    {
        const string ExpectedProviderName = "Gemini";

        ExpectedProviderName.Should().Be("Gemini");
    }
}
