using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Arkana.Gateway.Api.Endpoints;

/// <summary>
/// MCP (Model Context Protocol) server foundation — enables AI agents to interact with the gateway.
/// Phase 5 kickoff feature.
/// Implements JSON-RPC 2.0 transport per MCP specification.
/// </summary>
public static class McpServerEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static readonly JsonSerializerOptions JsonIndentedOptions = new() { WriteIndented = true };
    private static readonly string[] ValidSizes = ["256x256", "512x512", "1024x1024", "1024x1792", "1792x1024"];
    private static readonly string[] TextGenRequired = ["model", "prompt"];
    private static readonly string[] ChatRequired = ["model", "messages"];

    public static void MapMcpServerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/mcp").WithTags("MCP");

        // POST /mcp — JSON-RPC 2.0 endpoint (stateless)
        group.MapPost("/", async (HttpRequest request, ILogger<Program> logger,
            Arkana.Domain.Interfaces.IModelRepository modelRepo) =>
        {
            McpRequest? rpc;
            try
            {
                rpc = await request.ReadFromJsonAsync<McpRequest>();
            }
            catch (JsonException)
            {
                return Results.Json(new McpResponse
                {
                    Error = new McpError { Code = -32700, Message = "Parse error" }
                }, JsonOptions);
            }

            if (rpc is null)
                return Results.BadRequest(new { error = "Invalid JSON-RPC request" });

            return rpc.Method switch
            {
                "initialize" => HandleInitialize(rpc),
                "tools/list" => HandleToolsList(rpc),
                "tools/call" => await HandleToolsCall(rpc, logger, modelRepo),
                "resources/list" => HandleResourcesList(rpc),
                _ => Results.Json(new McpResponse
                {
                    Id = rpc.Id,
                    Error = new McpError { Code = -32601, Message = $"Method not found: {rpc.Method}" }
                }, JsonOptions)
            };
        })
        .WithName("McpServerEndpoint")
        .WithOpenApi();
    }

    // ── Handlers ──────────────────────────────────────────

    internal static IResult HandleInitialize(McpRequest rpc)
    {
        var result = new InitializeResult
        {
            ProtocolVersion = "2025-03-26",
            Capabilities = new McpCapabilities
            {
                Tools = new McpToolsCapability { ListChanged = true }
            },
            ServerInfo = new McpServerInfo
            {
                Name = "arkana-gateway",
                Version = "1.0.0"
            }
        };

        return Results.Json(new McpResponse { Id = rpc.Id, Result = result }, JsonOptions);
    }

    internal static IResult HandleToolsList(McpRequest rpc)
    {
        var tools = new List<McpTool>
        {
            new()
            {
                Name = "list_models",
                Description = "List available AI models in the gateway",
                InputSchema = new McpInputSchema { Type = "object", Properties = new Dictionary<string, object>() }
            },
            new()
            {
                Name = "generate_text",
                Description = "Generate text using a specified model",
                InputSchema = new McpInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, object>
                    {
                        ["model"] = new { type = "string", description = "Model identifier" },
                        ["prompt"] = new { type = "string", description = "The prompt text" },
                        ["max_tokens"] = new { type = "integer", description = "Maximum tokens to generate", @default = 256 }
                    },
                    Required = TextGenRequired
                }
            },
            new()
            {
                Name = "chat",
                Description = "Send a chat message using a specified model",
                InputSchema = new McpInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, object>
                    {
                        ["model"] = new { type = "string", description = "Model identifier" },
                        ["messages"] = new { type = "array", description = "Array of {role, content} messages" }
                    },
                    Required = ChatRequired
                }
            },
            new()
            {
                Name = "get_usage",
                Description = "Get usage statistics for a tenant",
                InputSchema = new McpInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, object>
                    {
                        ["tenant_id"] = new { type = "string", description = "Tenant identifier" }
                    }
                }
            }
        };

        return Results.Json(new McpResponse
        {
            Id = rpc.Id,
            Result = new { tools }
        }, JsonOptions);
    }

    internal static async Task<IResult> HandleToolsCall(McpRequest rpc, ILogger<Program> logger,
        Arkana.Domain.Interfaces.IModelRepository modelRepo)
    {
        var toolName = rpc.Params?.Name;
        var args = rpc.Params?.Arguments ?? default;

        if (string.IsNullOrEmpty(toolName))
        {
            return Results.Json(new McpResponse
            {
                Id = rpc.Id,
                Error = new McpError { Code = -32602, Message = "Missing tool name" }
            }, JsonOptions);
        }

        logger.LogInformation("MCP tool call: {Tool}", toolName);

        // Dispatch to tool handler
        var content = toolName switch
        {
            "list_models" => await HandleListModels(args, modelRepo),
            "get_usage" => await HandleGetUsage(args),
            "generate_text" => HandleStubTool("generate_text", "Text generation will be routed through the gateway."),
            "chat" => HandleStubTool("chat", "Chat will be routed through the gateway."),
            _ => new List<McpContent>
            {
                new() { Type = "text", Text = $"Error: Unknown tool: {toolName}" }
            }
        };

        var isError = content.Any(c => c.Text?.StartsWith("Error:") == true);

        return Results.Json(new McpResponse
        {
            Id = rpc.Id,
            Result = new McpToolResult
            {
                Content = content,
                IsError = isError
            }
        }, JsonOptions);
    }

    internal static IResult HandleResourcesList(McpRequest rpc)
    {
        var resources = new List<object>
        {
            new { uri = "gateway://models", name = "Available Models", mimeType = "application/json" },
            new { uri = "gateway://usage", name = "Usage Statistics", mimeType = "application/json" },
            new { uri = "gateway://templates", name = "Policy Templates", mimeType = "application/json" }
        };

        return Results.Json(new McpResponse
        {
            Id = rpc.Id,
            Result = new { resources }
        }, JsonOptions);
    }

    // ── Tool implementations ──────────────────────────────

    private static async Task<List<McpContent>> HandleListModels(JsonElement args,
        Arkana.Domain.Interfaces.IModelRepository modelRepo)
    {
        // Reflect the REAL catalog (mirrors GET /v1/models): only models that are
        // enabled AND belong to an enabled provider. Dead Codex marketing aliases
        // (gpt-5-codex / gpt-5.1-codex) are disabled in the DB, so they never
        // surface here — agents always see a working set.
        var all = await modelRepo.GetAllAsync();
        var models = all
            .Where(m => m.IsEnabled && m.Provider is { IsEnabled: true })
            .Select(m => new
            {
                id = m.Code,
                name = m.Name,
                provider = m.Provider.Name
            })
            .ToArray();

        return new List<McpContent>
        {
            new() { Type = "text", Text = JsonSerializer.Serialize(models, JsonIndentedOptions) }
        };
    }

    private static Task<List<McpContent>> HandleGetUsage(JsonElement args)
    {
        var tenantId = args.TryGetProperty("tenant_id", out var tEl) ? tEl.GetString() : "unknown";

        return Task.FromResult(new List<McpContent>
        {
            new()
            {
                Type = "text",
                Text = JsonSerializer.Serialize(new
                {
                    tenant_id = tenantId,
                    message = "Usage stats endpoint — connect to DashboardService for real data"
                }, JsonIndentedOptions)
            }
        });
    }

    private static List<McpContent> HandleStubTool(string toolName, string message)
    {
        return new List<McpContent>
        {
            new() { Type = "text", Text = $"[Stub] {toolName}: {message}" }
        };
    }
}

// ── MCP JSON-RPC models ─────────────────────────────────

public sealed class McpRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public McpParams? Params { get; set; }
}

public sealed class McpParams
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; set; }
}

public sealed class McpResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    public McpError? Error { get; set; }
}

public sealed class McpError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class InitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "";

    [JsonPropertyName("capabilities")]
    public McpCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("serverInfo")]
    public McpServerInfo ServerInfo { get; set; } = new();
}

public sealed class McpCapabilities
{
    [JsonPropertyName("tools")]
    public McpToolsCapability? Tools { get; set; }
}

public sealed class McpToolsCapability
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; set; }
}

public sealed class McpServerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}

public sealed class McpTool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("inputSchema")]
    public McpInputSchema InputSchema { get; set; } = new();
}

public sealed class McpInputSchema
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "object";

    [JsonPropertyName("properties")]
    public Dictionary<string, object> Properties { get; set; } = new();

    [JsonPropertyName("required")]
    public string[]? Required { get; set; }
}

public sealed class McpToolResult
{
    [JsonPropertyName("content")]
    public List<McpContent> Content { get; set; } = new();

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }
}

public sealed class McpContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}
