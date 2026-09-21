using System.Text.Json;

namespace Arkana.Domain.Interfaces;

/// <summary>
/// Client for communicating with external MCP (Model Context Protocol) servers.
/// Supports JSON-RPC 2.0 over HTTP/SSE transport per MCP specification 2025-03-26.
/// </summary>
public interface IMcpClient
{
    /// <summary>
    /// Connects to the remote MCP server and performs the initialize handshake.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Server info (name, version) and capabilities from the initialize response.</returns>
    Task<McpInitializeResult> ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists all tools exposed by the remote MCP server.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of tool definitions with name, description, and input schema.</returns>
    Task<IReadOnlyList<McpRemoteTool>> ListToolsAsync(CancellationToken ct = default);

    /// <summary>
    /// Calls a tool on the remote MCP server.
    /// </summary>
    /// <param name="toolName">Name of the tool to invoke.</param>
    /// <param name="arguments">Arguments to pass to the tool (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tool result containing content items and an error flag.</returns>
    Task<McpRemoteToolResult> CallToolAsync(string toolName, JsonElement? arguments = null, CancellationToken ct = default);

    /// <summary>
    /// Lists all resources exposed by the remote MCP server.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of resource definitions with URI, name, and MIME type.</returns>
    Task<IReadOnlyList<McpRemoteResource>> ListResourcesAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads a resource from the remote MCP server by URI.
    /// </summary>
    /// <param name="uri">The resource URI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Resource contents (text or binary).</returns>
    Task<McpRemoteResourceContent> ReadResourceAsync(string uri, CancellationToken ct = default);
}

// ── Shared DTOs used by IMcpClient ─────────────────────────────

public sealed class McpInitializeResult
{
    public string ProtocolVersion { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ServerVersion { get; set; } = "";
    public bool SupportsTools { get; set; }
    public bool SupportsResources { get; set; }
}

public sealed class McpRemoteTool
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string InputSchemaType { get; set; } = "object";
    public Dictionary<string, JsonElement>? Properties { get; set; }
    public string[]? Required { get; set; }
}

public sealed class McpRemoteToolResult
{
    public List<McpRemoteContent> Content { get; set; } = new();
    public bool IsError { get; set; }
}

public sealed class McpRemoteContent
{
    public string Type { get; set; } = "text";
    public string Text { get; set; } = "";
}

public sealed class McpRemoteResource
{
    public string Uri { get; set; } = "";
    public string Name { get; set; } = "";
    public string MimeType { get; set; } = "";
}

public sealed class McpRemoteResourceContent
{
    public string Uri { get; set; } = "";
    public string MimeType { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Blob { get; set; }
}
