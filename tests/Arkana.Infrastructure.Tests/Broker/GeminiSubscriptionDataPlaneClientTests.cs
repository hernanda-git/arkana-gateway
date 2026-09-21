using System.Net;
using System.Text;
using System.Text.Json;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.AI.Dialects;
using Arkana.Infrastructure.Broker;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.Broker;

public sealed class GeminiSubscriptionDataPlaneClientTests
{
    private static GeminiSubscriptionDataPlaneClient Sut(HttpMessageHandler handler, CLIProxySlotOptions? slot = null) =>
        new(Factory(handler), Options.Create(new CLIProxyManagementOptions
        {
            Slots = new Dictionary<string, CLIProxySlotOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["slot-a"] = slot ?? new() { BaseUrl = "https://broker-a.example/" }
            }
        }), new OpenAiDialectTranslator());

    private static IHttpClientFactory Factory(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("gemini-broker-data-plane").Returns(new HttpClient(handler));
        return factory;
    }

    [Fact]
    public async Task CompleteAsync_maps_openai_envelope_content_usage_and_tool_calls()
    {
        // Regression: the non-stream broker response is an OpenAI chat-completion
        // envelope. Parsing it directly into ChatResult dropped the visible content
        // and the usage counters (empty 200 responses, 0 tokens).
        var envelope = """
        {"id":"x","object":"chat.completion","model":"gemini-3-flash",
         "choices":[{"index":0,"message":{"role":"assistant","content":"READY",
           "tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Bandung\"}"}}]},
           "finish_reason":"stop"}],
         "usage":{"prompt_tokens":8,"completion_tokens":1,"total_tokens":9}}
        """;
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        });

        var result = await Sut(handler).CompleteAsync(Request("slot-a", "gemini-acc1"));

        result.Result.IsSuccess.Should().BeTrue();
        result.Result.Content.Should().Be("READY");
        result.Result.InputTokens.Should().Be(8);
        result.Result.OutputTokens.Should().Be(1);
        result.Result.ToolCalls.Should().ContainSingle();
        result.Result.ToolCalls![0].FunctionName.Should().Be("get_weather");
        result.Result.ToolCalls[0].FunctionArguments.Should().Contain("Bandung");
        result.Result.RouteKind.Should().Be("broker-managed");
        result.Result.ResolvedProviderAccountCode.Should().Be("gemini-acc1");
    }

    [Fact]
    public async Task CompleteAsync_rejects_a_200_without_a_usable_envelope()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"unexpected\":true}", Encoding.UTF8, "application/json")
        });

        var result = await Sut(handler).CompleteAsync(Request("slot-a", "gemini-acc1"));

        result.Result.IsSuccess.Should().BeFalse();
        result.Result.ErrorMessage.Should().Be("Gemini subscription upstream returned an invalid result.");
        result.Result.UpstreamStatus.Should().Be(502);
    }

    [Fact]
    public async Task StreamAsync_posts_stream_request_to_the_allowlisted_slot()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("data: {}\n\ndata: [DONE]\n\n")))
        });
        var sut = Sut(handler);

        await using var result = await sut.StreamAsync(Request("slot-a", "gemini-acc1"));

        result.IsSuccess.Should().BeTrue();
        result.Stream.Should().NotBeNull();
        handler.Request.RequestUri.Should().Be(new Uri("https://broker-a.example/v1/chat/completions"));
        handler.Request.Headers.GetValues("X-Logical-Provider").Single().Should().Be("gemini");
        handler.Request.Headers.GetValues("X-Provider-Account").Single().Should().Be("gemini-acc1");
        handler.Request.Headers.GetValues("X-Broker-Slot").Single().Should().Be("slot-a");

        using var document = JsonDocument.Parse(handler.Body!);
        document.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task StreamAsync_sanitizes_non_success_body()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("access_token=secret slot_url=https://internal.example")
        });
        var sut = Sut(handler);

        await using var result = await sut.StreamAsync(Request("slot-a", "gemini-acc1"));

        result.IsSuccess.Should().BeFalse();
        result.Result.ErrorMessage.Should().Be("Gemini subscription rate limit exceeded.");
        result.Result.ErrorMessage.Should().NotContain("access_token");
        result.Result.ErrorMessage.Should().NotContain("internal.example");
        result.Result.UpstreamStatus.Should().Be(429);
    }

    [Fact]
    public async Task CompleteAsync_forwards_tool_conversation_in_the_openai_wire_shape()
    {
        // Regression: the broker speaks OpenAI wire names (`tool_calls` /
        // `tool_call_id`). CamelCased domain serialization made it silently
        // drop the tool exchange, so the upstream re-issued the same call
        // instead of consuming the tool result.
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}",
                Encoding.UTF8,
                "application/json")
        });
        var request = new GeminiBrokerRequest(
            new ChatRequest
            {
                Model = "gemini-2.5-pro",
                Messages =
                [
                    new ChatMessage { Role = "user", Content = "weather in Tokyo?" },
                    new ChatMessage
                    {
                        Role = "assistant",
                        Content = "",
                        ToolCalls =
                        [
                            new ToolCall
                            {
                                Id = "get_weather-1-1",
                                Type = "function",
                                Function = new ToolCallFunction { Name = "get_weather", Arguments = "{\"city\":\"Tokyo\"}" }
                            }
                        ]
                    },
                    new ChatMessage { Role = "tool", Content = "{\"temp_c\":22}", ToolCallId = "get_weather-1-1" }
                ]
            },
            "slot-a", "gemini-acc1", "trace-test", "gemini");

        await Sut(handler).CompleteAsync(request);

        using var document = JsonDocument.Parse(handler.Body!);
        var messages = document.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(3);

        var assistant = messages[1];
        var call = assistant.GetProperty("tool_calls")[0];
        call.GetProperty("id").GetString().Should().Be("get_weather-1-1");
        call.GetProperty("type").GetString().Should().Be("function");
        call.GetProperty("function").GetProperty("name").GetString().Should().Be("get_weather");
        call.GetProperty("function").GetProperty("arguments").GetString().Should().Contain("Tokyo");

        var tool = messages[2];
        tool.GetProperty("role").GetString().Should().Be("tool");
        tool.GetProperty("tool_call_id").GetString().Should().Be("get_weather-1-1");

        handler.Body.Should().NotContain("toolCalls").And.NotContain("toolCallId");
    }

    [Fact]
    public async Task StreamAsync_sends_the_slot_data_plane_key_when_configured()
    {
        // Hardening: a broker slot that enforces `api-keys` must receive the key, otherwise every
        // completion is rejected with 401 and the gateway reports "upstream request failed (HTTP 401)".
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("data: [DONE]\n\n")))
        });
        var slot = new CLIProxySlotOptions
        {
            BaseUrl = "https://broker-a.example/",
            ManagementKey = "management-key",
            DataPlaneKey = "data-plane-key"
        };

        await using var result = await Sut(handler, slot).StreamAsync(Request("slot-a", "gemini-acc1"));

        result.IsSuccess.Should().BeTrue();
        handler.Request.Headers.GetValues("Authorization").Single().Should().Be("Bearer data-plane-key");
    }

    [Fact]
    public async Task StreamAsync_falls_back_to_the_management_key_as_the_data_plane_key()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("data: [DONE]\n\n")))
        });
        var slot = new CLIProxySlotOptions { BaseUrl = "https://broker-a.example/", ManagementKey = "single-secret" };

        await using var result = await Sut(handler, slot).StreamAsync(Request("slot-a", "gemini-acc1"));

        result.IsSuccess.Should().BeTrue();
        handler.Request.Headers.GetValues("Authorization").Single().Should().Be("Bearer single-secret");
    }

    [Fact]
    public async Task CompleteAsync_omits_authorization_when_the_slot_has_no_key()
    {
        // A slot configured without any key keeps the pre-hardening posture (broker trusts the
        // container network); sending an empty bearer would make the broker reject the call.
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}",
                Encoding.UTF8,
                "application/json")
        });

        await Sut(handler).CompleteAsync(Request("slot-a", "gemini-acc1"));

        handler.Request.Headers.Contains("Authorization").Should().BeFalse();
    }

    private static GeminiBrokerRequest Request(string slot, string account) => new(
        new ChatRequest
        {
            Model = "gemini-2.5-pro",
            Messages = [new ChatMessage { Role = "user", Content = "hello" }]
        }, slot, account, "trace-test", "gemini");

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage Request { get; private set; } = null!;
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
