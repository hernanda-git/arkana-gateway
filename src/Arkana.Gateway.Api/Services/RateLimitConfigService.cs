using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Arkana.Gateway.Api.Services;

/// <summary>
/// DB-backed rate limit config — reads global defaults from
/// <see cref="GlobalRateLimit"/> and per-key overrides from
/// <see cref="ApiKey.RateLimitRpm"/>/<see cref="ApiKey.RateLimitTpm"/>/<see cref="ApiKey.RateLimitMaxConcurrent"/>.
/// Cached for 30s to keep the hot path fast.
/// Implements <see cref="IRateLimitConfigProvider"/> for the
/// middleware hot path, and exposes CRUD methods for the Settings UI.
///
/// Singleton-safe: uses <see cref="IServiceScopeFactory"/> to create
/// short-lived scopes for each DB access, so the 30s cache refresh
/// doesn't hold a DbContext open.
/// </summary>
public sealed class RateLimitConfigService : IRateLimitConfigProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RateLimitConfigService> _logger;

    public RateLimitConfigService(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<RateLimitConfigService> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
    }

    // ── IRateLimitConfigProvider (hot path) ──────────────────

    public async Task<bool> IsEnabledAsync()
    {
        var g = await GetGlobalAsync();
        return g.Enabled;
    }

    public async Task<(int Rpm, int Tpm, int MaxConcurrent)> GetDefaultsAsync()
    {
        var g = await GetGlobalAsync();
        return (g.DefaultRequestsPerMinute, g.DefaultTokensPerMinute, g.DefaultMaxConcurrent);
    }

    public async Task<(int? Rpm, int? Tpm, int? MaxConcurrent)> GetKeyOverridesAsync(string apiKeyName)
    {
        var cacheKey = $"rate:key:{apiKeyName}";
        if (_cache.TryGetValue(cacheKey, out (int?, int?, int?) cached))
            return cached;

        using var scope = _scopeFactory.CreateScope();
        var keys = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
        var allKeys = await keys.GetAllAsync();
        var key = allKeys.FirstOrDefault(k =>
            k.Name.Equals(apiKeyName, StringComparison.OrdinalIgnoreCase));

        var result = (key?.RateLimitRpm, key?.RateLimitTpm, key?.RateLimitMaxConcurrent);
        _cache.Set(cacheKey, result, TimeSpan.FromSeconds(30));
        return result;
    }

    // ── Global defaults CRUD (Settings UI) ───────────────────

    private async Task<GlobalRateLimit> GetGlobalAsync()
    {
        const string key = "rate:global";
        if (_cache.TryGetValue(key, out GlobalRateLimit? cached) && cached is not null)
            return cached;

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGlobalRateLimitRepository>();
        var row = await repo.GetAsync();
        var result = row ?? GlobalRateLimit.Default;

        _cache.Set(key, result, TimeSpan.FromSeconds(30));
        return result;
    }

    public async Task<int> GetDefaultRpmAsync()
        => (await GetGlobalAsync()).DefaultRequestsPerMinute;

    public async Task<int> GetDefaultTpmAsync()
        => (await GetGlobalAsync()).DefaultTokensPerMinute;

    public async Task<int> GetDefaultMaxConcurrentAsync()
        => (await GetGlobalAsync()).DefaultMaxConcurrent;

    public async Task<bool> GetEnabledAsync()
        => (await GetGlobalAsync()).Enabled;

    public async Task SaveGlobalConfigAsync(bool enabled, int rpm, int tpm, int maxConcurrent)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGlobalRateLimitRepository>();

        var config = GlobalRateLimit.Create();
        config.Enabled = enabled;
        config.DefaultRequestsPerMinute = rpm;
        config.DefaultTokensPerMinute = tpm;
        config.DefaultMaxConcurrent = maxConcurrent;
        await repo.UpsertAsync(config);
        _cache.Remove("rate:global");
    }

    // ── Cache invalidation ──────────────────────────────────

    public void InvalidateKeyCache(string apiKeyName)
        => _cache.Remove($"rate:key:{apiKeyName}");

    public void InvalidateAll()
        => _cache.Remove("rate:global");
}
