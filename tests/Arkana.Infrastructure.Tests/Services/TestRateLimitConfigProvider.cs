using Arkana.Domain.Interfaces;

namespace Arkana.Infrastructure.Tests.Services;

/// <summary>
/// Test double for <see cref="IRateLimitConfigProvider"/>.
/// Defaults match <see cref="Domain.Entities.GlobalRateLimit.Default"/>.
/// </summary>
public sealed class TestRateLimitConfigProvider : IRateLimitConfigProvider
{
    public bool Enabled { get; set; } = true;
    public int DefaultRpm { get; set; } = 60;
    public int DefaultTpm { get; set; } = 1_000_000;
    public int DefaultMaxConcurrent { get; set; } = 5;

    /// <summary>Per-key overrides. Key = api key name (case-insensitive).</summary>
    public Dictionary<string, (int? Rpm, int? Tpm, int? MaxConcurrent)> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> IsEnabledAsync() => Task.FromResult(Enabled);
    public Task<(int Rpm, int Tpm, int MaxConcurrent)> GetDefaultsAsync()
        => Task.FromResult((DefaultRpm, DefaultTpm, DefaultMaxConcurrent));

    public Task<(int? Rpm, int? Tpm, int? MaxConcurrent)> GetKeyOverridesAsync(string apiKeyName)
    {
        if (Overrides.TryGetValue(apiKeyName, out var ov))
            return Task.FromResult(ov);
        return Task.FromResult<(int?, int?, int?)>((null, null, null));
    }
}
