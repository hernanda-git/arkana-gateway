namespace Arkana.Domain.Entities;

/// <summary>
/// Webhook subscription for receiving real-time event notifications (Phase 5).
/// Supports tenant scoping and event filtering with automatic retry on failure.
/// Events: agent.completed, workflow.completed, token.threshold, error.rate
/// </summary>
public sealed class Webhook
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Url { get; private set; } = string.Empty;
    public string Secret { get; private set; } = string.Empty;

    /// <summary>JSON-serialized array of event types this webhook subscribes to.</summary>
    public string Events { get; private set; } = "[]";

    public bool IsActive { get; private set; }
    public int RetryCount { get; private set; } = 3;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastTriggeredAt { get; private set; }
    public int FailureCount { get; private set; }

    // Navigation
    public Tenant Tenant { get; private set; } = null!;

    private Webhook() { } // EF Core

    /// <summary>
    /// Creates a new webhook subscription.
    /// </summary>
    /// <param name="tenantId">The tenant this webhook belongs to.</param>
    /// <param name="url">The HTTPS endpoint to receive webhook payloads.</param>
    /// <param name="secret">HMAC secret for payload signature verification.</param>
    /// <param name="events">Event types to subscribe to (e.g. agent.completed).</param>
    /// <param name="retryCount">Max retry attempts on failure (default 3).</param>
    public static Webhook Create(
        Guid tenantId,
        string url,
        string secret,
        string[] events,
        int retryCount = 3)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        return new Webhook
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Url = url,
            Secret = secret,
            Events = System.Text.Json.JsonSerializer.Serialize(events),
            IsActive = true,
            RetryCount = retryCount,
            CreatedAt = DateTimeOffset.UtcNow,
            FailureCount = 0
        };
    }

    /// <summary>
    /// Parses the serialized Events property into an array of event type strings.
    /// </summary>
    public string[] GetEvents()
        => System.Text.Json.JsonSerializer.Deserialize<string[]>(Events) ?? [];

    /// <summary>Updates the webhook URL.</summary>
    public void UpdateUrl(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        Url = url;
    }

    /// <summary>Updates the webhook secret.</summary>
    public void UpdateSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        Secret = secret;
    }

    /// <summary>Updates the subscribed event types.</summary>
    public void UpdateEvents(string[] events)
    {
        Events = System.Text.Json.JsonSerializer.Serialize(events);
    }

    /// <summary>Updates the retry count.</summary>
    public void UpdateRetryCount(int retryCount)
    {
        RetryCount = retryCount;
    }

    /// <summary>Records a successful trigger, resetting the failure count.</summary>
    public void RecordSuccess()
    {
        LastTriggeredAt = DateTimeOffset.UtcNow;
        FailureCount = 0;
    }

    /// <summary>Records a failed trigger attempt.</summary>
    public void RecordFailure()
    {
        FailureCount++;
        LastTriggeredAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Activates the webhook.</summary>
    public void Activate() => IsActive = true;

    /// <summary>Deactivates the webhook.</summary>
    public void Deactivate() => IsActive = false;
}
