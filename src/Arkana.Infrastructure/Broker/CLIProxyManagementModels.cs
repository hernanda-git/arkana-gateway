namespace Arkana.Infrastructure.Broker;

public sealed class CLIProxyManagementOptions
{
    public const string Section = "GeminiBroker";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxResponseBytes { get; set; } = 256 * 1024;
    public string DefaultSlot { get; set; } = "gemini-broker-a";
    public Dictionary<string, CLIProxySlotOptions> Slots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CLIProxySlotOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ManagementKey { get; set; } = string.Empty;

    /// <summary>
    /// Key the broker requires on its data plane (<c>api-keys</c> in the broker config). When set,
    /// every broker completion/stream call carries <c>Authorization: Bearer &lt;key&gt;</c>, so a slot
    /// can be locked down instead of trusting the container network. Falls back to
    /// <see cref="ManagementKey"/> when unset, which is the single-secret default the slot generator
    /// and the deploy templates ship; set it explicitly to split the two planes.
    /// </summary>
    public string DataPlaneKey { get; set; } = string.Empty;

    /// <summary>The key actually sent on data-plane calls (never empty unless the slot has no key at all).</summary>
    public string EffectiveDataPlaneKey =>
        string.IsNullOrWhiteSpace(DataPlaneKey) ? ManagementKey : DataPlaneKey;

    public string? FutureConsolidationPrefix { get; set; }
}

public sealed record CLIProxyOAuthStart(string Slot, string AuthorizationUrl, DateTimeOffset ExpiresAt);
public enum CLIProxyOAuthCallbackDisposition { Accepted, AlreadyProcessed }
public sealed record CLIProxyOAuthCallbackResult(CLIProxyOAuthCallbackDisposition Disposition);
public sealed record CLIProxyOAuthStatus(string Slot, string State, DateTimeOffset? ExpiresAt, string? StableAuthId);
public sealed record CLIProxyAuthFile(string StableAuthId, bool Disabled, string? Provider, string? AccountLabel);
public sealed record CLIProxyAuthFiles(string Slot, IReadOnlyList<CLIProxyAuthFile> Files);

public interface ICLIProxyManagementClientFactory
{
    ICLIProxyManagementClient Create(string slot);
}

public interface ICLIProxyManagementClient
{
    Task<CLIProxyOAuthStart> StartOAuthAsync(CancellationToken ct = default);
    Task<CLIProxyOAuthCallbackResult> SubmitOAuthCallbackAsync(string state, string? code, string? oauthError, CancellationToken ct = default);
    Task<CLIProxyOAuthStatus> GetOAuthStatusAsync(CancellationToken ct = default);
    Task<CLIProxyAuthFiles> ListAuthFilesAsync(CancellationToken ct = default);
    Task DisableAsync(string stableAuthId, CancellationToken ct = default);
    Task EnableAsync(string stableAuthId, CancellationToken ct = default);
    /// <summary>Reserved for a future, explicitly configured consolidation API; never inferred from account codes.</summary>
    Task<IReadOnlyList<CLIProxyAuthFile>> ListByFuturePrefixAsync(string prefix, CancellationToken ct = default);
    Task DeleteAsync(string stableAuthId, CancellationToken ct = default);
}

public static class CLIProxyAuthIdentifier
{
    public static string Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value == "." ||
            value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')) ||
            value.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Stable broker auth identifier is invalid.", nameof(value));
        return value;
    }
}
