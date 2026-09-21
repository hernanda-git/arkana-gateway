using System.Net.Http.Json;
using System.Text.Json;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// Communicates with n8n's internal REST API using cookie-based session authentication.
/// Logs in once (lazy on first call) and reuses the session for the lifetime of the service.
/// Registered as singleton to keep the cookie container and authenticated state across calls.
/// </summary>
public sealed class N8nService : IN8nService, IDisposable
{
    private readonly HttpClient _http;
    private readonly N8nOptions _options;
    private readonly ILogger<N8nService> _log;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private bool _authenticated;
    private bool _disposed;

    // Shared HttpClientHandler with a persistent cookie container — needed because
    // the typed-AddHttpClient registration was transient, causing a new session
    // (and a new /rest/login call) per injection, which quickly hit n8n's rate limit.
    private static readonly HttpClientHandler _sharedHandler = new()
    {
        AllowAutoRedirect = true,
        UseCookies = true,
        CookieContainer = new System.Net.CookieContainer()
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public N8nService(IOptions<N8nOptions> options, ILogger<N8nService> log)
    {
        _http = new HttpClient(_sharedHandler, disposeHandler: false);
        _options = options.Value;
        _log = log;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
    }

    // ── Public API ────────────────────────────────────────────

    public async Task<IReadOnlyList<WorkflowInfo>> GetWorkflowsAsync(CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var raw = await _http.GetFromJsonAsync<N8nWorkflowListResponse>("rest/workflows", JsonOpts, ct);
        return raw?.Data?.Select(MapWorkflow).ToList() ?? [];
    }

    public async Task<WorkflowInfo?> GetWorkflowAsync(string workflowId, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var raw = await _http.GetFromJsonAsync<N8nWorkflowResponse>($"rest/workflows/{workflowId}", JsonOpts, ct);
        return raw?.Data is not null ? MapWorkflow(raw.Data) : null;
    }

    public async Task<WorkflowExecutionInfo> ExecuteWorkflowAsync(string workflowId, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);

        // Fetch workflow data to build the run payload
        var wfResponse = await _http.GetFromJsonAsync<N8nWorkflowResponse>(
            $"rest/workflows/{workflowId}", JsonOpts, ct);
        var wf = wfResponse?.Data;
        if (wf is null)
            throw new InvalidOperationException($"Workflow {workflowId} not found");

        // Build the run payload (same format n8n editor uses)
        var startNode = wf.Nodes?.FirstOrDefault();
        var runPayload = new
        {
            workflowData = new
            {
                wf.Id,
                wf.Name,
                wf.Nodes,
                wf.Connections,
                wf.Settings,
                wf.StaticData,
                PinData = new Dictionary<string, object>(),
                wf.VersionId
            },
            startNodes = startNode is not null ? new[] { startNode.Name } : Array.Empty<string>(),
            runData = new Dictionary<string, object>(),
            pushRef = "gateway"
        };

        var response = await _http.PostAsJsonAsync($"rest/workflows/{workflowId}/run", runPayload, JsonOpts, ct);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadFromJsonAsync<N8nExecutionResponse>(JsonOpts, ct);
        return MapExecution(raw?.Data ?? new N8nExecutionData { Id = workflowId, Status = "running", StartedAt = DateTimeOffset.UtcNow });
    }

    public async Task ActivateWorkflowAsync(string workflowId, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        // Fetch workflow to get versionId
        var wfResponse = await _http.GetFromJsonAsync<N8nWorkflowResponse>(
            $"rest/workflows/{workflowId}", JsonOpts, ct);
        var versionId = wfResponse?.Data?.VersionId;
        var body = versionId is not null ? new { versionId } : null;
        var response = await _http.PostAsJsonAsync($"rest/workflows/{workflowId}/activate", body, JsonOpts, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeactivateWorkflowAsync(string workflowId, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var response = await _http.PostAsync($"rest/workflows/{workflowId}/deactivate", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<WorkflowExecutionInfo>> GetExecutionsAsync(int limit = 20, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var raw = await _http.GetFromJsonAsync<N8nExecutionListResponse>(
            $"rest/executions?limit={limit}&includeData=false", JsonOpts, ct);
        return raw?.Data?.Results?.Select(MapExecution).ToList() ?? [];
    }

    public async Task<WorkflowExecutionInfo?> GetExecutionAsync(string executionId, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var raw = await _http.GetFromJsonAsync<N8nExecutionResponse>($"rest/executions/{executionId}", JsonOpts, ct);
        return raw?.Data is not null ? MapExecution(raw.Data) : null;
    }

    // ── Authentication ────────────────────────────────────────

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (_authenticated) return;

        await _authLock.WaitAsync(ct);
        try
        {
            if (_authenticated) return;

            if (string.IsNullOrEmpty(_options.UserEmail) || string.IsNullOrEmpty(_options.UserPassword))
            {
                _log.LogWarning("n8n UserEmail/UserPassword not configured — using unauthenticated requests");
                _authenticated = true;
                return;
            }

            var loginBody = new
            {
                emailOrLdapLoginId = _options.UserEmail,
                password = _options.UserPassword
            };

            var response = await _http.PostAsJsonAsync("rest/login", loginBody, JsonOpts, ct);
            response.EnsureSuccessStatusCode();

            // The .NET HttpClientHandler automatically manages cookies from Set-Cookie headers
            _authenticated = true;
            _log.LogInformation("n8n authentication successful");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to authenticate with n8n at {BaseUrl}", _options.BaseUrl);
            throw;
        }
        finally
        {
            _authLock.Release();
        }
    }

    // ── Mapping ───────────────────────────────────────────────

    private WorkflowInfo MapWorkflow(N8nWorkflowData wf)
    {
        var triggerType = DetectTriggerType(wf);
        var cronExpr = ExtractCronExpression(wf);
        var webhookUrl = ExtractWebhookUrl(wf);
        var tags = wf.Tags?.Select(t => t.Name) ?? [];

        return new WorkflowInfo(
            wf.Id, wf.Name, wf.Active,
            wf.CreatedAt, wf.UpdatedAt,
            triggerType, cronExpr, webhookUrl,
            wf.Nodes?.Count ?? 0, tags);
    }

    private static WorkflowExecutionInfo MapExecution(N8nExecutionData exe)
    {
        return new WorkflowExecutionInfo(
            exe.Id ?? "",
            exe.WorkflowId ?? "",
            exe.WorkflowName ?? "",
            exe.Status ?? "unknown",
            exe.StartedAt,
            exe.FinishedAt,
            exe.Mode,
            exe.ErrorMessage);
    }

    // ── Trigger detection ─────────────────────────────────────

    private static WorkflowTriggerType DetectTriggerType(N8nWorkflowData wf)
    {
        if (wf.Nodes is null || wf.Nodes.Count == 0)
            return WorkflowTriggerType.Manual;

        var hasCron = false;
        var hasWebhook = false;
        var hasManual = false;

        foreach (var node in wf.Nodes)
        {
            var type = node.Type ?? "";
            if (type.Contains("scheduleTrigger", StringComparison.OrdinalIgnoreCase) ||
                type.Contains(".cron", StringComparison.OrdinalIgnoreCase))
                hasCron = true;
            else if (type.Contains("webhook", StringComparison.OrdinalIgnoreCase) ||
                     type.Contains("formTrigger", StringComparison.OrdinalIgnoreCase))
                hasWebhook = true;
            else if (type.Contains("manualTrigger", StringComparison.OrdinalIgnoreCase))
                hasManual = true;
        }

        var count = (hasCron ? 1 : 0) + (hasWebhook ? 1 : 0) + (hasManual ? 1 : 0);
        return count switch
        {
            > 1 => WorkflowTriggerType.Mixed,
            _ when hasCron => WorkflowTriggerType.Cron,
            _ when hasWebhook => WorkflowTriggerType.Webhook,
            _ => WorkflowTriggerType.Manual
        };
    }

    private static string? ExtractCronExpression(N8nWorkflowData wf)
    {
        var cronNode = wf.Nodes?.FirstOrDefault(n =>
            (n.Type ?? "").Contains("scheduleTrigger", StringComparison.OrdinalIgnoreCase) ||
            (n.Type ?? "").Contains(".cron", StringComparison.OrdinalIgnoreCase));

        return cronNode?.Parameters?.TryGetValue("expression", out var expr) == true && expr is string exprStr
            ? exprStr
            : cronNode?.Parameters?.TryGetValue("cronExpression", out var cs) == true && cs is string cronStr
                ? cronStr
                : null;
    }

    private static string? ExtractWebhookUrl(N8nWorkflowData wf)
    {
        var webhookNode = wf.Nodes?.FirstOrDefault(n =>
            (n.Type ?? "").Contains("webhook", StringComparison.OrdinalIgnoreCase));

        if (webhookNode?.Parameters is null) return null;

        if (webhookNode.Parameters.TryGetValue("url", out var url) && url is string urlStr)
            return urlStr;

        if (webhookNode.Parameters.TryGetValue("path", out var path) && path is string pathStr)
            return $"/webhook/{pathStr}";

        return null;
    }
    // ── IDisposable ──────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed)
        {
            _authLock.Dispose();
            _disposed = true;
        }
    }
}

// ══════════════════════════════════════════════════════════════
// n8n API response DTOs (internal — not exposed outside service)
// ══════════════════════════════════════════════════════════════

internal sealed record N8nWorkflowListResponse(List<N8nWorkflowData>? Data);
internal sealed record N8nWorkflowResponse(N8nWorkflowData? Data);

internal sealed record N8nWorkflowData
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<N8nNodeData>? Nodes { get; set; }
    public List<N8nTagData>? Tags { get; set; }
    public Dictionary<string, object>? Settings { get; set; }
    public Dictionary<string, object>? StaticData { get; set; }
    public Dictionary<string, object>? Connections { get; set; }
    public string? VersionId { get; set; }
}

internal sealed record N8nNodeData
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string? Type { get; set; }
    public object? TypeVersion { get; set; }
    public Dictionary<string, object>? Parameters { get; set; }
    public double[]? Position { get; set; }
}

internal sealed record N8nTagData
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

internal sealed record N8nExecutionListResponse(N8nExecutionListData? Data);

internal sealed record N8nExecutionListData
{
    public List<N8nExecutionData>? Results { get; set; }
}
internal sealed record N8nExecutionResponse(N8nExecutionData? Data);

internal sealed record N8nExecutionData
{
    public string? Id { get; set; }
    public string? WorkflowId { get; set; }
    public string? WorkflowName { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public DateTimeOffset? FinishedAt => StoppedAt;
    public string? Mode { get; set; }
    public string? ErrorMessage { get; set; }
}
