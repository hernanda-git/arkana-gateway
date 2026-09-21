using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arkana.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arkana.Infrastructure.Mcp;

/// <summary>
/// MCP (Model Context Protocol) client that connects to external MCP servers
/// via HTTP+SSE transport. Implements the JSON-RPC 2.0 wire protocol per
/// MCP specification version 2025-03-26.
/// </summary>
public sealed class McpClient : IMcpClient
{
    private readonly HttpClient _http;
    private readonly ILogger<McpClient> _logger;
    private readonly McpClientOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private long _requestId;

    public McpClient(
        HttpClient http,
        ILogger<McpClient> logger,
        IOptions<McpClientOptions> options)
    {
        _http = http;
        _logger = logger;
        _options = options.Value;

        _http.Timeout = _options.Timeout;
    }

    /// <inheritdoc/>
    public async Task<McpInitializeResult> ConnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Connecting to MCP server at {Url}", _options.Url);

        var request = BuildRequest("initialize", new
        {
            protocolVersion = "2025-03-26",
            capabilities = new { },
            clientInfo = new
            {
                name = _options.ClientName,
                version = _options.ClientVersion
            }
        });

        var response = await SendAsync(request, ct);
        var result = response.GetProperty("result");

        var initializeResult = new McpInitializeResult
        {
            ProtocolVersion = result.GetProperty("protocolVersion").GetString() ?? "",
            ServerName = result.TryGetProperty("serverInfo", out var si)
                ? si.TryGetProperty("name", out var name) ? name.GetString() ?? "" : ""
                : "",
            ServerVersion = result.TryGetProperty("serverInfo", out var si2)
                ? si2.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : ""
                : "",
            SupportsTools = result.TryGetProperty("capabilities", out var caps)
                && caps.TryGetProperty("tools", out _),
            SupportsResources = result.TryGetProperty("capabilities", out var caps2)
                && caps2.TryGetProperty("resources", out _)
        };

        _logger.LogInformation("Connected to MCP server {Name} v{Version} (protocol {Protocol})",
            initializeResult.ServerName, initializeResult.ServerVersion, initializeResult.ProtocolVersion);

        return initializeResult;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<McpRemoteTool>> ListToolsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Listing tools from MCP server");

        var request = BuildRequest("tools/list");
        var response = await SendAsync(request, ct);

        var tools = new List<McpRemoteTool>();

        if (response.TryGetProperty("result", out var result)
            && result.TryGetProperty("tools", out var toolsArray))
        {
            foreach (var tool in toolsArray.EnumerateArray())
            {
                var remoteTool = new McpRemoteTool
                {
                    Name = tool.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Description = tool.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ""
                };

                if (tool.TryGetProperty("inputSchema", out var schema))
                {
                    remoteTool.InputSchemaType = schema.TryGetProperty("type", out var t)
                        ? t.GetString() ?? "object" : "object";

                    if (schema.TryGetProperty("properties", out var props))
                    {
                        remoteTool.Properties = new Dictionary<string, JsonElement>();
                        foreach (var prop in props.EnumerateObject())
                        {
                            remoteTool.Properties[prop.Name] = prop.Value.Clone();
                        }
                    }

                    if (schema.TryGetProperty("required", out var req))
                    {
                        var requiredList = new List<string>();
                        foreach (var r in req.EnumerateArray())
                        {
                            requiredList.Add(r.GetString() ?? "");
                        }
                        remoteTool.Required = requiredList.ToArray();
                    }
                }

                tools.Add(remoteTool);
            }
        }

        _logger.LogInformation("Discovered {Count} tools from MCP server", tools.Count);
        return tools;
    }

    /// <inheritdoc/>
    public async Task<McpRemoteToolResult> CallToolAsync(
        string toolName,
        JsonElement? arguments = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Calling tool '{ToolName}' on MCP server", toolName);

        var callParams = new Dictionary<string, object?>
        {
            ["name"] = toolName,
            ["arguments"] = arguments.HasValue
                ? JsonSerializer.SerializeToElement(arguments.Value, JsonOptions)
                : JsonDocument.Parse("{}").RootElement
        };

        var request = BuildRequest("tools/call", callParams);
        var response = await SendAsync(request, ct);

        var toolResult = new McpRemoteToolResult();

        if (response.TryGetProperty("result", out var result))
        {
            toolResult.IsError = result.TryGetProperty("isError", out var isError)
                && isError.GetBoolean();

            if (result.TryGetProperty("content", out var contentArray))
            {
                foreach (var contentItem in contentArray.EnumerateArray())
                {
                    toolResult.Content.Add(new McpRemoteContent
                    {
                        Type = contentItem.TryGetProperty("type", out var ct2)
                            ? ct2.GetString() ?? "text" : "text",
                        Text = contentItem.TryGetProperty("text", out var text)
                            ? text.GetString() ?? "" : ""
                    });
                }
            }
        }
        else if (response.TryGetProperty("error", out var error))
        {
            toolResult.IsError = true;
            var errorMessage = error.TryGetProperty("message", out var msg)
                ? msg.GetString() ?? "Unknown error" : "Unknown error";
            toolResult.Content.Add(new McpRemoteContent
            {
                Type = "text",
                Text = $"Error: {errorMessage}"
            });
        }

        _logger.LogDebug("Tool '{ToolName}' completed (isError={IsError})",
            toolName, toolResult.IsError);

        return toolResult;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<McpRemoteResource>> ListResourcesAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Listing resources from MCP server");

        var request = BuildRequest("resources/list");
        var response = await SendAsync(request, ct);

        var resources = new List<McpRemoteResource>();

        if (response.TryGetProperty("result", out var result)
            && result.TryGetProperty("resources", out var resourcesArray))
        {
            foreach (var resource in resourcesArray.EnumerateArray())
            {
                resources.Add(new McpRemoteResource
                {
                    Uri = resource.TryGetProperty("uri", out var uri) ? uri.GetString() ?? "" : "",
                    Name = resource.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    MimeType = resource.TryGetProperty("mimeType", out var mime) ? mime.GetString() ?? "" : ""
                });
            }
        }

        _logger.LogInformation("Discovered {Count} resources from MCP server", resources.Count);
        return resources;
    }

    /// <inheritdoc/>
    public async Task<McpRemoteResourceContent> ReadResourceAsync(string uri, CancellationToken ct = default)
    {
        _logger.LogInformation("Reading resource '{Uri}' from MCP server", uri);

        var request = BuildRequest("resources/read", new { uri });
        var response = await SendAsync(request, ct);

        var content = new McpRemoteResourceContent { Uri = uri };

        if (response.TryGetProperty("result", out var result)
            && result.TryGetProperty("contents", out var contentsArray))
        {
            foreach (var item in contentsArray.EnumerateArray())
            {
                if (item.TryGetProperty("uri", out var u))
                    content.Uri = u.GetString() ?? uri;

                if (item.TryGetProperty("mimeType", out var mime))
                    content.MimeType = mime.GetString() ?? "";

                if (item.TryGetProperty("text", out var text))
                    content.Text = text.GetString() ?? "";

                if (item.TryGetProperty("blob", out var blob))
                    content.Blob = blob.GetString();

                break; // Take the first content item
            }
        }

        return content;
    }

    // ── Private helpers ───────────────────────────────────────

    private McpJsonRpcRequest BuildRequest(string method, object? parameters = null)
    {
        var id = Interlocked.Increment(ref _requestId);
        return new McpJsonRpcRequest
        {
            Id = id,
            Method = method,
            Params = parameters
        };
    }

    private async Task<JsonElement> SendAsync(McpJsonRpcRequest rpcRequest, CancellationToken ct)
    {
        _logger.LogDebug("Sending JSON-RPC request: id={Id} method={Method}", rpcRequest.Id, rpcRequest.Method);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.Url)
        {
            Content = JsonContent.Create(rpcRequest, options: JsonOptions)
        };

        httpRequest.Headers.Add("Accept", "application/json");

        var response = await _http.SendAsync(httpRequest, ct);

        if (response.StatusCode == HttpStatusCode.NotFound
            || response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            throw new InvalidOperationException(
                $"MCP server at {_options.Url} returned {(int)response.StatusCode}. " +
                "Ensure the server is running and the URL is correct.");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            throw new InvalidOperationException(
                $"MCP server returned empty response for method '{rpcRequest.Method}'.");
        }

        var json = JsonDocument.Parse(responseBody);

        if (json.RootElement.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

            throw new McpRpcException(code, message, rpcRequest.Method);
        }

        return json.RootElement.Clone();
    }
}

// ── Internal models ───────────────────────────────────────

internal sealed class McpJsonRpcRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public long Id { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("params")]
    public object? Params { get; set; }
}

/// <summary>
/// Exception thrown when a remote MCP server returns a JSON-RPC error response.
/// </summary>
internal sealed class McpRpcException : Exception
{
    public int ErrorCode { get; }
    public string RpcMethod { get; }

    public McpRpcException(int errorCode, string message, string rpcMethod)
        : base($"MCP RPC error {errorCode} on '{rpcMethod}': {message}")
    {
        ErrorCode = errorCode;
        RpcMethod = rpcMethod;
    }
}
