namespace Arkana.Infrastructure.Mcp;

/// <summary>
/// Transport type for MCP client connections.
/// </summary>
public enum McpTransportType
{
    /// <summary>HTTP+SSE (Server-Sent Events) transport — communicates over HTTP POST + SSE stream.</summary>
    Sse,

    /// <summary>Stdio transport — communicates via stdin/stdout with a child process.</summary>
    Stdio
}

/// <summary>
/// Configuration options for connecting to an external MCP server.
/// </summary>
public sealed class McpClientOptions
{
    /// <summary>Configuration section name for binding from appsettings.json.</summary>
    public const string Section = "McpClient";

    /// <summary>
    /// Transport type to use when connecting to the MCP server.
    /// Default is <see cref="McpTransportType.Sse"/> (HTTP).
    /// </summary>
    public McpTransportType TransportType { get; set; } = McpTransportType.Sse;

    /// <summary>
    /// Base URL of the remote MCP server (for SSE/HTTP transport).
    /// Example: "https://example.com/mcp".
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// Command to launch for stdio transport (e.g. "npx", "node").
    /// Ignored when <see cref="TransportType"/> is <see cref="McpTransportType.Sse"/>.
    /// </summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// Arguments to pass to the stdio command.
    /// Ignored when <see cref="TransportType"/> is <see cref="McpTransportType.Sse"/>.
    /// </summary>
    public string[] Arguments { get; set; } = [];

    /// <summary>
    /// Request timeout for JSON-RPC calls to the remote server.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Client name sent during the MCP initialize handshake.
    /// Default is "arkana-client".
    /// </summary>
    public string ClientName { get; set; } = "arkana-client";

    /// <summary>
    /// Client version sent during the MCP initialize handshake.
    /// Default is "1.0.0".
    /// </summary>
    public string ClientVersion { get; set; } = "1.0.0";
}
