namespace Arkana.Infrastructure.Services;

/// <summary>
/// Configuration options for connecting to the n8n REST API.
/// Bound from the "N8n" section of appsettings.json.
/// </summary>
public sealed class N8nOptions
{
    public const string Section = "N8n";

    /// <summary>Base URL of the n8n instance (e.g. "http://localhost:5678").</summary>
    public string BaseUrl { get; set; } = "http://localhost:5678";

    /// <summary>
    /// n8n user email for cookie-based session authentication.
    /// Required for workflow execution, activation, and execution history.
    /// </summary>
    public string UserEmail { get; set; } = string.Empty;

    /// <summary>
    /// n8n user password for cookie-based session authentication.
    /// </summary>
    public string UserPassword { get; set; } = string.Empty;
}
