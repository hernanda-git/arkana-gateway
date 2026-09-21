using Arkana.Domain.Services;

namespace Arkana.Domain.Entities;

/// <summary>
/// A pool of API keys for a single AI provider, enabling round-robin rotation
/// on rate limits (429) and automatic failover. Each provider can have multiple
/// keys distributed across accounts to multiply throughput.
///
/// Example: OpenAI free tier has 50 RPD per key. With 8 keys in the pool,
/// the gateway can handle 400 requests/day.
/// </summary>
public sealed class ApiKeyPool
{
    public Guid Id { get; set; }
    public Guid AiProviderId { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>Current rotation index — advances on each GetNextKey call.</summary>
    public int ActiveIndex { get; set; }

    /// <summary>Total requests made across all keys in this pool.</summary>
    public long TotalRequests { get; set; }

    /// <summary>Total rate-limit hits (429) across all keys.</summary>
    public long TotalRateLimits { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    // Navigation
    public AiProvider Provider { get; set; } = null!;
    public Tenant Tenant { get; set; } = null!;
    public ICollection<ApiKeyPoolEntry> Entries { get; set; } = [];

    private ApiKeyPool() { } // EF Core

    public static ApiKeyPool Create(Guid aiProviderId, Guid tenantId)
    {
        return new ApiKeyPool
        {
            Id = Guid.NewGuid(),
            AiProviderId = aiProviderId,
            TenantId = tenantId,
            ActiveIndex = 0,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Advance to the next key index (round-robin).</summary>
    public int AdvanceIndex()
    {
        var entries = Entries.Where(e => e.IsActive).ToList();
        if (entries.Count == 0) return ActiveIndex;
        ActiveIndex = (ActiveIndex + 1) % entries.Count;
        return ActiveIndex;
    }
}

/// <summary>
/// A single API key entry within an <see cref="ApiKeyPool"/>.
/// Each entry is envelope-encrypted and tracks its own rate-limit state.
/// </summary>
public sealed class ApiKeyPoolEntry
{
    public Guid Id { get; set; }
    public Guid PoolId { get; set; }

    /// <summary>Sealed (envelope-encrypted) API key.</summary>
    public string SealedKey { get; set; } = string.Empty;

    /// <summary>Display label (e.g., "key_0", "personal", "team-account").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Rotation ordering — lower = tried first.</summary>
    public int Priority { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>When this key was last rate-limited. Null = never or cooldown expired.</summary>
    public DateTimeOffset? RateLimitedAt { get; set; }

    /// <summary>Cooldown period before retrying a rate-limited key (default 60s).</summary>
    public int CooldownSeconds { get; set; } = 60;

    /// <summary>Consecutive rate-limit hits for this key (resets on successful use).</summary>
    public int ConsecutiveRateLimits { get; set; }

    /// <summary>Total requests made with this key.</summary>
    public long RequestCount { get; set; }

    /// <summary>Total rate-limit hits for this key.</summary>
    public long RateLimitCount { get; set; }

    /// <summary>Permanently disabled (e.g. insufficient_quota). Not eligible for rotation.</summary>
    public bool IsPermanentlyDisabled { get; set; }

    /// <summary>Last error type returned by OpenAI (e.g. "insufficient_quota", "rate_limit_exceeded").</summary>
    public string? LastErrorType { get; set; }

    /// <summary>Model codes this key can access (e.g. ["gpt-5.4-mini","gpt-4o"]).
    /// Empty array or null = key supports all models (legacy/unfiltered).
    /// Stored as JSON array in the database via EF Core value converter.</summary>
    public string[]? AllowedModels { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Navigation
    public ApiKeyPool Pool { get; set; } = null!;

    private ApiKeyPoolEntry() { } // EF Core

    public static ApiKeyPoolEntry Create(string sealedKey, string label, int priority = 0, int cooldownSeconds = 60)
    {
        return new ApiKeyPoolEntry
        {
            Id = Guid.NewGuid(),
            SealedKey = sealedKey,
            Label = label,
            Priority = priority,
            IsActive = true,
            CooldownSeconds = cooldownSeconds,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Mark this key as rate-limited.</summary>
    /// <param name="errorType">OpenAI error type (e.g. "insufficient_quota", "rate_limit_exceeded").</param>
    public void MarkRateLimited(string? errorType = null)
    {
        RateLimitedAt = DateTimeOffset.UtcNow;
        ConsecutiveRateLimits++;
        RateLimitCount++;
        LastErrorType = errorType;

        // Permanent failure — account out of credits
        if (string.Equals(errorType, "insufficient_quota", StringComparison.OrdinalIgnoreCase))
        {
            IsPermanentlyDisabled = true;
        }
    }

    /// <summary>Reset rate-limit state after a successful use.</summary>
    public void MarkSuccess()
    {
        ConsecutiveRateLimits = 0;
        LastErrorType = null;
        RequestCount++;
    }

    /// <summary>Whether this key is eligible for rotation (active, not permanently disabled).</summary>
    public bool IsEligible => IsActive && !IsPermanentlyDisabled;

    /// <summary>Whether this key supports a given model code. Null or empty AllowedModels = supports all.</summary>
    public bool SupportsModel(string modelCode)
    {
        if (AllowedModels is null or { Length: 0 }) return true;
        return AllowedModels.Contains(modelCode, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether this key is currently in cooldown (rate-limited and not yet ready).</summary>
    public bool IsInCooldown =>
        RateLimitedAt.HasValue &&
        DateTimeOffset.UtcNow < RateLimitedAt.Value.AddSeconds(CooldownSeconds);
}
