using System.Net;
using System.Text.Json;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.Mcp;
using Arkana.Infrastructure.Tests.AI;
#pragma warning disable CA1861 // Test file — inline arrays in anonymous types are acceptable
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.Mcp;

public class McpClientTests
{
    private readonly ILogger<McpClient> _logger;
    private readonly McpClientOptions _options;

    public McpClientTests()
    {
        _logger = Substitute.For<ILogger<McpClient>>();
        _options = new McpClientOptions
        {
            Url = "http://localhost:5000/mcp",
            ClientName = "test-client",
            ClientVersion = "0.1.0"
        };
    }

    private McpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var mockHandler = new MockHttpMessageHandler(handler);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };
        return new McpClient(httpClient, _logger, Options.Create(_options));
    }

    private static readonly JsonSerializerOptions s_jsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string JsonResponse(object obj)
        => JsonSerializer.Serialize(obj, s_jsonOpts);

    // ── ConnectAsync tests ──────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_returns_server_info()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonResponse(new
            {
                jsonrpc = "2.0",
                id = 1,
                result = new
                {
                    protocolVersion = "2025-03-26",
                    capabilities = new
                    {
                        tools = new { listChanged = true },
                        resources = new { }
                    },
                    serverInfo = new
                    {
                        name = "test-server",
                        version = "2.0.0"
                    }
                }
            }))
        });

        var result = await client.ConnectAsync();

        result.Should().NotBeNull();
        result.ProtocolVersion.Should().Be("2025-03-26");
        result.ServerName.Should().Be("test-server");
        result.ServerVersion.Should().Be("2.0.0");
        result.SupportsTools.Should().BeTrue();
        result.SupportsResources.Should().BeTrue();
    }

    [Fact]
    public async Task ConnectAsync_handles_missing_capabilities()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonResponse(new
            {
                jsonrpc = "2.0",
                id = 1,
                result = new
                {
                    protocolVersion = "2025-03-26",
                    capabilities = new { },
                    serverInfo = new { name = "minimal-server", version = "0.1.0" }
                }
            }))
        });

        var result = await client.ConnectAsync();

        result.ServerName.Should().Be("minimal-server");
        result.SupportsTools.Should().BeFalse();
        result.SupportsResources.Should().BeFalse();
    }

    [Fact]
    public async Task ConnectAsync_throws_on_http_error()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = () => client.ConnectAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── ListToolsAsync tests ────────────────────────────────────

    [Fact]
    public async Task ListToolsAsync_returns_tools()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonResponse(new
            {
                jsonrpc = "2.0",
                id = 1,
                result = new
                {
                    tools = new object[]
                    {
                        new
                        {
                            name = "list_models",
                            description = "List available AI models",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new { },
                                required = Array.Empty<string>()
                            }
                        },
                        new
                        {
                            name = "generate_text",
                            description = "Generate text",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    model = new { type = "string", description = "Model name" },
                                    prompt = new { type = "string", description = "The prompt" }
                                },
                                required = new[] { "model", "prompt" }
                            }
                        }
                    }
                }
            }))
        });

        var tools = await client.ListToolsAsync();

        tools.Should().HaveCount(2);
        tools[0].Name.Should().Be("list_models");
        tools[0].Description.Should().Be("List available AI models");
        tools[0].Properties.Should().BeEmpty();

        tools[1].Name.Should().Be("generate_text");
        tools[1].Description.Should().Be("Generate text");
        tools[1].Properties.Should().HaveCount(2);
        tools[1].Required.Should().ContainInOrder("model", "prompt");
    }

    [Fact]
    public async Task ListToolsAsync_returns_empty_when_no_tools()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonResponse(new
            {
                jsonrpc = "2.0",
                id = 1,
                result = new { tools = Array.Empty<object>() }
            }))
        });

        var tools = await client.ListToolsAsync();

        tools.Should().BeEmpty();
    }

    // ── CallToolAsync tests ─────────────────────────────────────

    [Fact]
    public async Task CallToolAsync_returns_tool_result()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonResponse(new
            {
                jsonrpc = "2.0",
                id = 1,
                result = new
                {
                    content = new[]
                    {
                        new { type = "text", text = "{\"status\":\"ok\"}" }
                    },
                    isError = false
                }
            }))
        });

        var result = await client.CallToolAsync("list_models");

        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();
        result.Content.Should().HaveCount(1);
        result.Content[0].Type.Should().Be("text");
        result.Content[0].Text.Should().Be("{\"status\":\"ok\"}");
    }

    [Fact]
    public async Task CallToolAsync_passes_arguments()
    {
        HttpRequestMessage? capturedRequest = null;

        var client = CreateClient(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonResponse(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    result = new
                    {
                        content = new[] { new { type = "text", text = "done" } },
                        isError = false
                    }
                }))
            };
        });

        var args = JsonDocument.Parse("{\"model\":\"gpt-4o\",\"prompt\":\"Hello\"}").RootElement;
        await client.CallToolAsync("generate_text", args);

        capturedRequest.Should().NotBeNull();
        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        body.Should().Contain("generate_text");
        body.Should().Contain("gpt-4o");
        body.Should().Contain("Hello");
    }

    [Fact]
    public async Task CallToolAsync_handles_error_response()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonResponse(new
            {
                jsonrpc = "2.0",
                id = 1,
                result = new
                {
                    content = new[] { new { type = "text", text = "Error: tool failed" } },
                    isError = true
                }
            }))
        });

        var result = await client.CallToolAsync("failing_tool");

        result.IsError.Should().BeTrue();
        result.Content.Should().HaveCount(1);
        result.Content[0].Text.Should().Contain("Error: tool failed");
    }

    [Fact]
    public async Task CallToolAsync_handles_rpc_error()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonResponse(new
            {
                jsonrpc = "2.0",
                id = 1,
                error = new
                {
                    code = -32601,
                    message = "Method not found"
                }
            }))
        });

        var act = () => client.CallToolAsync("nonexistent");

        var ex = await act.Should().ThrowAsync<McpRpcException>();
        ex.Which.ErrorCode.Should().Be(-32601);
        ex.Which.RpcMethod.Should().Be("tools/call");
    }

    // ── ListResourcesAsync tests ────────────────────────────────

    [Fact]
    public async Task ListResourcesAsync_returns_resources()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonResponse(new
            {
                jsonrpc = "2.0",
                id = 1,
                result = new
                {
                    resources = new[]
                    {
                        new { uri = "gateway://models", name = "Available Models", mimeType = "application/json" },
                        new { uri = "gateway://usage", name = "Usage Statistics", mimeType = "application/json" }
                    }
                }
            }))
        });

        var resources = await client.ListResourcesAsync();

        resources.Should().HaveCount(2);
        resources[0].Uri.Should().Be("gateway://models");
        resources[0].Name.Should().Be("Available Models");
        resources[0].MimeType.Should().Be("application/json");
        resources[1].Uri.Should().Be("gateway://usage");
    }

    [Fact]
    public async Task ListResourcesAsync_returns_empty_when_no_resources()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonResponse(new
            {
                jsonrpc = "2.0",
                id = 1,
                result = new { resources = Array.Empty<object>() }
            }))
        });

        var resources = await client.ListResourcesAsync();

        resources.Should().BeEmpty();
    }

    // ── ReadResourceAsync tests ─────────────────────────────────

    [Fact]
    public async Task ReadResourceAsync_returns_content()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonResponse(new
            {
                jsonrpc = "2.0",
                id = 1,
                result = new
                {
                    contents = new[]
                    {
                        new
                        {
                            uri = "gateway://models",
                            mimeType = "application/json",
                            text = "[{\"id\":\"gpt-4o\"}]"
                        }
                    }
                }
            }))
        });

        var content = await client.ReadResourceAsync("gateway://models");

        content.Should().NotBeNull();
        content.Uri.Should().Be("gateway://models");
        content.MimeType.Should().Be("application/json");
        content.Text.Should().Be("[{\"id\":\"gpt-4o\"}]");
        content.Blob.Should().BeNull();
    }

    [Fact]
    public async Task ReadResourceAsync_handles_empty_response()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonResponse(new
            {
                jsonrpc = "2.0",
                id = 1,
                result = new { contents = Array.Empty<object>() }
            }))
        });

        var content = await client.ReadResourceAsync("gateway://nonexistent");

        content.Uri.Should().Be("gateway://nonexistent");
        content.Text.Should().BeEmpty();
    }

    // ── McpClientOptions tests ──────────────────────────────────

    [Fact]
    public void McpClientOptions_default_values_are_correct()
    {
        var options = new McpClientOptions();

        options.TransportType.Should().Be(McpTransportType.Sse);
        options.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        options.ClientName.Should().Be("arkana-client");
        options.ClientVersion.Should().Be("1.0.0");
        options.Url.Should().BeEmpty();
        options.Command.Should().BeEmpty();
        options.Arguments.Should().BeEmpty();
    }

    // ── JSON-RPC 2.0 wire format tests ──────────────────────────

    [Fact]
    public async Task SendAsync_sends_jsonrpc_2_0_request()
    {
        HttpRequestMessage? capturedRequest = null;

        var client = CreateClient(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonResponse(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    result = new
                    {
                        protocolVersion = "2025-03-26",
                        capabilities = new { },
                        serverInfo = new { name = "test-server", version = "1.0" }
                    }
                }))
            };
        });

        await client.ConnectAsync();

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Method.Should().Be(HttpMethod.Post);

        var body = await capturedRequest.Content!.ReadAsStringAsync();
        body.Should().Contain("\"jsonrpc\":\"2.0\"");
        body.Should().Contain("\"method\":\"initialize\"");
        body.Should().Contain("\"protocolVersion\":\"2025-03-26\"");
    }

    [Fact]
    public async Task SendAsync_returns_unique_ids_per_request()
    {
        long? firstId = null;
        long? secondId = null;
        var callCount = 0;

        var client = CreateClient(req =>
        {
            callCount++;
            var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.GetProperty("id").GetInt64();

            if (callCount == 1) firstId = id;
            else if (callCount == 2) secondId = id;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonResponse(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        tools = Array.Empty<object>(),
                        protocolVersion = "2025-03-26",
                        capabilities = new { },
                        serverInfo = new { name = "x", version = "1.0.0" }
                    }
                }))
            };
        });

        await client.ConnectAsync();
        await client.ListToolsAsync();

        firstId.Should().NotBeNull();
        secondId.Should().NotBeNull();
        secondId.Should().Be(firstId!.Value + 1);
    }

    [Fact]
    public async Task SendAsync_throws_on_empty_response()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("")
        });

        var act = () => client.ConnectAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*empty response*");
    }

    [Fact]
    public async Task SendAsync_throws_on_not_found()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = () => client.ConnectAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*404*");
    }
}
