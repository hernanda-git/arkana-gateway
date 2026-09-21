using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkana.Gateway.Api.Services;

/// <summary>
/// Supported webhook event types (Phase 5).
/// </summary>
public interface IWebhookDispatcher
{
    /// <summary>Enqueues a webhook event for asynchronous delivery.</summary>
    ValueTask<bool> EnqueueAsync(WebhookEvent webhookEvent, CancellationToken ct = default);
}

public static class WebhookEvents
{
    public const string AgentCompleted = "agent.completed";
    public const string WorkflowCompleted = "workflow.completed";
    public const string TokenThreshold = "token.threshold";
    public const string ErrorRate = "error.rate";

    public static readonly string[] All = [AgentCompleted, WorkflowCompleted, TokenThreshold, ErrorRate];
}

/// <summary>
/// A webhook event payload to be dispatched to subscribed endpoints.
/// </summary>
public sealed record WebhookEvent(
    string EventType,
    Guid EventId,
    DateTimeOffset Timestamp,
    object Data);

/// <summary>
/// Background service that dispatches webhook events to registered endpoints.
/// Uses Channel&lt;T&gt; for background queue processing, exponential backoff
/// retry logic, and idempotency via event ID tracking.
/// </summary>
public sealed class WebhookDispatcher : BackgroundService, IWebhookDispatcher
{
    private readonly System.Threading.Channels.Channel<WebhookEvent> _channel;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookDispatcher> _logger;

    // Idempotency: track processed event IDs to prevent duplicate delivery.
    // In a production system this would use Redis or a database table.
    private readonly ConcurrentDictionary<string, bool> _processedEvents = new();
    private const int MaxProcessedEventCacheSize = 10_000;

    // Exponential backoff settings
    private const int BaseDelayMs = 1000;
    private const int MaxDelayMs = 30_000;

    public WebhookDispatcher(
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookDispatcher> logger)
    {
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _channel = System.Threading.Channels.Channel.CreateBounded<WebhookEvent>(
            new System.Threading.Channels.BoundedChannelOptions(500)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    /// <summary>
    /// Enqueues a webhook event for asynchronous delivery.
    /// Returns false if the queue is full (event dropped).
    /// </summary>
    public async ValueTask<bool> EnqueueAsync(WebhookEvent webhookEvent, CancellationToken ct = default)
    {
        // Idempotency check — skip if already processed or enqueued
        var eventKey = $"{webhookEvent.EventType}:{webhookEvent.EventId}";
        if (!_processedEvents.TryAdd(eventKey, true))
        {
            _logger.LogDebug("Skipping duplicate webhook event {EventId}", webhookEvent.EventId);
            return false;
        }

        // Evict cache if too large (simple approach for in-memory tracking)
        if (_processedEvents.Count > MaxProcessedEventCacheSize)
        {
            // Clear oldest entries — in production, use Redis TTL or a database.
            _processedEvents.Clear();
            _processedEvents.TryAdd(eventKey, true);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            if (await _channel.Writer.WaitToWriteAsync(cts.Token))
            {
                if (_channel.Writer.TryWrite(webhookEvent))
                {
                    _logger.LogDebug("Enqueued webhook event {EventType}:{EventId}", webhookEvent.EventType, webhookEvent.EventId);
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Internal timeout — drop the event
        }

        _logger.LogWarning("Webhook queue full; dropped event {EventType}:{EventId}", webhookEvent.EventType, webhookEvent.EventId);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebhookDispatcher started, processing events from background queue");

        await foreach (var webhookEvent in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await DispatchToSubscribersAsync(webhookEvent, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dispatching webhook event {EventType}:{EventId}",
                    webhookEvent.EventType, webhookEvent.EventId);
            }
        }

        _logger.LogInformation("WebhookDispatcher stopped");
    }

    private async Task DispatchToSubscribersAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var webhookRepo = scope.ServiceProvider.GetRequiredService<IWebhookRepository>();

        // Get all active webhooks — in production, filter by TenantId if available
        var allWebhooks = await webhookRepo.GetAllAsync();
        var matchingWebhooks = allWebhooks
            .Where(w => w.IsActive && w.GetEvents().Contains(webhookEvent.EventType))
            .ToList();

        if (matchingWebhooks.Count == 0)
        {
            _logger.LogDebug("No active webhooks for event {EventType}", webhookEvent.EventType);
            return;
        }

        _logger.LogInformation("Dispatching event {EventType}:{EventId} to {Count} webhook(s)",
            webhookEvent.EventType, webhookEvent.EventId, matchingWebhooks.Count);

        var payload = JsonSerializer.Serialize(new
        {
            event_type = webhookEvent.EventType,
            event_id = webhookEvent.EventId,
            timestamp = webhookEvent.Timestamp,
            data = webhookEvent.Data
        });

        // Dispatch concurrently to all matching webhooks
        var tasks = matchingWebhooks.Select(w => DispatchWithRetryAsync(w, payload, webhookEvent.EventId.ToString(), ct));
        await Task.WhenAll(tasks);
    }

    private async Task DispatchWithRetryAsync(Webhook webhook, string payload, string eventId, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("webhook");
        var maxRetries = webhook.RetryCount;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };

                // HMAC-SHA256 signature for payload verification
                var signature = ComputeHmacSha256(webhook.Secret, payload);
                request.Headers.Add("X-Webhook-Signature", $"sha256={signature}");
                request.Headers.Add("X-Webhook-Event", eventId);
                request.Headers.Add("X-Webhook-Delivery", Guid.NewGuid().ToString("N"));

                var response = await client.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    webhook.RecordSuccess();
                    _logger.LogDebug("Webhook {WebhookId} delivered successfully (attempt {Attempt})",
                        webhook.Id, attempt + 1);
                    return;
                }

                _logger.LogWarning("Webhook {WebhookId} returned {StatusCode} (attempt {Attempt})",
                    webhook.Id, response.StatusCode, attempt + 1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Webhook {WebhookId} delivery failed (attempt {Attempt})",
                    webhook.Id, attempt + 1);
            }

            webhook.RecordFailure();

            // Exponential backoff: delay = BaseDelay * 2^attempt, capped at MaxDelay
            if (attempt < maxRetries)
            {
                var delayMs = Math.Min(BaseDelayMs * (1 << attempt), MaxDelayMs);
                await Task.Delay(delayMs, ct);
            }
        }

        _logger.LogError("Webhook {WebhookId} exhausted all {MaxRetries} retry attempts for event {EventId}",
            webhook.Id, maxRetries, eventId);
    }

    /// <summary>
    /// Computes HMAC-SHA256 signature for webhook payload verification.
    /// </summary>
    internal static string ComputeHmacSha256(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
