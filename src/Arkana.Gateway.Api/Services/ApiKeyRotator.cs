using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;

namespace Arkana.Gateway.Api.Services;

/// <summary>
/// Result of a key rotation request — the decrypted key plus metadata
/// needed by the caller to track which key was used.
/// </summary>
public sealed record RotatedKey(
    string PlaintextKey,
    Guid EntryId,
    int Index,
    string Label
);

/// <summary>
/// API key rotator — manages round-robin rotation across a pool of keys
/// for a single provider. Handles rate-limit detection, cooldown tracking,
/// and automatic key advancement.
///
/// Flow:
///   1. Caller requests next key via GetNextKeyAsync(providerCode, tenantId)
///   2. Rotator finds the pool, skips keys in cooldown, returns the next available key
///   3. On 429, caller reports via ReportRateLimitAsync(entryId)
///   4. On success, caller reports via ReportSuccessAsync(entryId)
/// </summary>
public sealed class ApiKeyRotator : IApiKeyRotator
{
    private readonly IApiKeyPoolRepository _poolRepo;
    private readonly ICredentialVault _vault;
    private readonly ILogger<ApiKeyRotator> _logger;

    public ApiKeyRotator(
        IApiKeyPoolRepository poolRepo,
        ICredentialVault vault,
        ILogger<ApiKeyRotator> logger)
    {
        _poolRepo = poolRepo;
        _vault = vault;
        _logger = logger;
    }

    /// <summary>
    /// Get the next available (non-cooldown, non-disabled) key for a provider.
    /// Optionally filter by model code — returns only keys that support the requested model.
    /// Returns null if no keys are available.
    /// </summary>
    public async Task<RotatedKey?> GetNextKeyAsync(Guid providerId, Guid tenantId, string? modelCode = null, CancellationToken ct = default)
    {
        var pool = await _poolRepo.GetByProviderAsync(providerId, tenantId, ct);
        if (pool == null || !pool.IsActive) return null;

        // Filter: eligible (active + not permanently disabled) + supports model
        var activeEntries = pool.Entries
            .Where(e => e.IsEligible && (modelCode == null || e.SupportsModel(modelCode)))
            .OrderBy(e => e.Priority)
            .ToList();

        if (activeEntries.Count == 0)
        {
            _logger.LogWarning("No eligible keys for provider {ProviderId} model {Model}", providerId, modelCode ?? "*");
            return null;
        }

        // Find first eligible key (not in cooldown, not disabled), starting from ActiveIndex
        for (int i = 0; i < activeEntries.Count; i++)
        {
            var idx = (pool.ActiveIndex + i) % activeEntries.Count;
            var entry = activeEntries[idx];

            if (!entry.IsInCooldown)
            {
                // Advance pool index for next call
                pool.ActiveIndex = (idx + 1) % activeEntries.Count;
                pool.TotalRequests++;
                entry.RequestCount++;

                await _poolRepo.UpdateAsync(pool, ct);

                var plaintext = entry.SealedKey.StartsWith("v1:")
                    ? _vault.Open(entry.SealedKey)
                    : entry.SealedKey; // Legacy plaintext

                _logger.LogDebug("Rotated to key[{Index}] '%27{Label}' for provider {ProviderId}",
                    idx, entry.Label, providerId);

                return new RotatedKey(plaintext!, entry.Id, idx, entry.Label);
            }
        }

        // All keys in cooldown — try the oldest cooldown entry anyway
        var fallback = activeEntries.OrderBy(e => e.RateLimitedAt).First();
        var fbIdx = activeEntries.IndexOf(fallback);

        pool.ActiveIndex = (fbIdx + 1) % activeEntries.Count;
        pool.TotalRequests++;
        await _poolRepo.UpdateAsync(pool, ct);

        var fbPlaintext = fallback.SealedKey.StartsWith("v1:")
            ? _vault.Open(fallback.SealedKey)
            : fallback.SealedKey;

        _logger.LogWarning("All keys for provider {ProviderId} in cooldown, using oldest: key[{Index}] '{Label}'",
            providerId, fbIdx, fallback.Label);

        return new RotatedKey(fbPlaintext!, fallback.Id, fbIdx, fallback.Label);
    }

    /// <summary>Report a rate-limit hit (429) for a specific key entry.</summary>
    /// <param name="errorType">OpenAI error type from response (e.g. "insufficient_quota", "rate_limit_exceeded").</param>
    public async Task ReportRateLimitAsync(Guid entryId, string? errorType = null, CancellationToken ct = default)
    {
        var pool = await FindPoolByEntryAsync(entryId, ct);
        if (pool == null) return;

        var entry = pool.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry == null) return;

        entry.MarkRateLimited(errorType);
        pool.TotalRateLimits++;

        await _poolRepo.UpdateAsync(pool, ct);

        if (entry.IsPermanentlyDisabled)
            _logger.LogError("Key[{Index}] '%27{Label}' PERMANENTLY DISABLED — {ErrorType}",
                pool.Entries.ToList().IndexOf(entry), entry.Label, errorType);
        else
            _logger.LogWarning("Key[{Index}] '%27{Label}' rate-limited (consecutive: {Count}), cooldown {Seconds}s",
                pool.Entries.ToList().IndexOf(entry), entry.Label, entry.ConsecutiveRateLimits, entry.CooldownSeconds);
    }

    /// <summary>Report a successful use for a specific key entry.</summary>
    public async Task ReportSuccessAsync(Guid entryId, CancellationToken ct = default)
    {
        var pool = await FindPoolByEntryAsync(entryId, ct);
        if (pool == null) return;

        var entry = pool.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry == null) return;

        entry.MarkSuccess();
        await _poolRepo.UpdateAsync(pool, ct);
    }

    /// <summary>Get pool status for admin display.</summary>
    public async Task<ApiKeyPool?> GetPoolAsync(Guid providerId, Guid tenantId, CancellationToken ct = default)
    {
        return await _poolRepo.GetByProviderAsync(providerId, tenantId, ct);
    }

    private async Task<ApiKeyPool?> FindPoolByEntryAsync(Guid entryId, CancellationToken ct)
    {
        // Scan all pools for this tenant — small N, acceptable
        var pools = await _poolRepo.GetAllAsync(Guid.Empty, ct);
        return pools.FirstOrDefault(p => p.Entries.Any(e => e.Id == entryId));
    }
}

/// <summary>Interface for API key rotation.</summary>
public interface IApiKeyRotator
{
    Task<RotatedKey?> GetNextKeyAsync(Guid providerId, Guid tenantId, string? modelCode = null, CancellationToken ct = default);
    Task ReportRateLimitAsync(Guid entryId, string? errorType = null, CancellationToken ct = default);
    Task ReportSuccessAsync(Guid entryId, CancellationToken ct = default);
    Task<ApiKeyPool?> GetPoolAsync(Guid providerId, Guid tenantId, CancellationToken ct = default);
}
