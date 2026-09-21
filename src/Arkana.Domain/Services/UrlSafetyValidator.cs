using System.Net;
using System.Net.Sockets;

namespace Arkana.Domain.Services;

/// <summary>
/// Validates URLs to prevent Server-Side Request Forgery (SSRF) when the
/// gateway makes outbound calls to AI provider endpoints whose base URL
/// is stored in the database (AiProviders.BaseUrl).
///
/// What it blocks:
///   - Non-HTTPS schemes in production (http, ftp, file, gopher, etc.)
///   - Loopback addresses (127.0.0.0/8, ::1)
///   - Private network ranges (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, fc00::/7)
///   - Link-local (169.254.0.0/16, fe80::/10) — covers cloud metadata services
///   - Multicast / broadcast / reserved ranges
///   - Cloud metadata hostnames (169.254.169.254, metadata.google.internal, etc.)
///   - Hosts whose DNS resolves to any of the above
///
/// Configuration:
///   - AllowHttp: only enable in development/test
///   - AllowPrivateAddresses: only enable in test (e.g., CLIProxyAPI on localhost)
///   - AdditionalBlockedHosts: append to the default blocklist
/// </summary>
public sealed class UrlSafetyValidator
{
    private readonly UrlSafetyOptions _options;
    private readonly DnsResolver _dns;

    // Cloud metadata hostnames — explicit blocklist for the common providers.
    // Defense-in-depth: even if DNS is somehow bypassed, these strings are rejected.
    private static readonly string[] KnownMetadataHosts =
    [
        "169.254.169.254",                // AWS, GCP, Azure, Oracle, Alibaba (instance metadata)
        "metadata.google.internal",       // GCP
        "metadata.azure.com",             // Azure (newer)
        "metadata.aliyun.com",            // Alibaba Cloud (newer)
        "100.100.100.200",                // Alibaba Cloud (legacy)
        "169.254.170.2",                  // AWS ECS task metadata
        "localhost",
        "0.0.0.0"
    ];

    public UrlSafetyValidator(UrlSafetyOptions options, DnsResolver dns)
    {
        _options = options;
        _dns = dns;
    }

    /// <summary>
    /// Validate that a URL is safe to make an outbound HTTP request to.
    /// </summary>
    /// <returns>The normalized <see cref="Uri"/> if safe.</returns>
    /// <exception cref="UrlSafetyException">The URL is unsafe; see message for the specific reason.</exception>
    public async Task<Uri> ValidateAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new UrlSafetyException("URL is empty.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new UrlSafetyException($"URL is not a valid absolute URI: {url}");

        // ── Scheme check ──
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("https" or "http"))
            throw new UrlSafetyException($"Scheme '{scheme}' is not allowed. Only http(s) are permitted.");

        if (scheme == "http" && !_options.AllowHttp)
            throw new UrlSafetyException("HTTP is not allowed in this environment. Use HTTPS.");

        // ── Hostname blocklist (fast path before DNS) ──
        var host = uri.Host;
        foreach (var blocked in KnownMetadataHosts)
        {
            if (host.Equals(blocked, StringComparison.OrdinalIgnoreCase))
                throw new UrlSafetyException($"Host '{host}' is in the blocklist (cloud metadata / loopback).");
        }

        if (_options.AdditionalBlockedHosts is { } extra)
        {
            foreach (var blocked in extra)
            {
                if (host.Equals(blocked, StringComparison.OrdinalIgnoreCase))
                    throw new UrlSafetyException($"Host '{host}' is in the operator blocklist.");
            }
        }

        // ── IP literal check ──
        if (IPAddress.TryParse(host, out var literalIp))
        {
            if (IsDisallowedAddress(literalIp))
                throw new UrlSafetyException($"IP literal '{host}' is in a disallowed range.");
        }
        else
        {
            // Resolve all addresses; if ANY is disallowed, reject.
            // DNS results can change between calls (rebinding attacks), so this is
            // a snapshot — the HttpClient itself may still rebind. Production
            // deployments should also pin resolved IPs in the HttpClient handler.
            var addresses = await _dns.ResolveAllAsync(host, ct);
            foreach (var addr in addresses)
            {
                if (IsDisallowedAddress(addr))
                    throw new UrlSafetyException(
                        $"Host '{host}' resolves to disallowed address {addr}.");
            }
        }

        return uri;
    }

    /// <summary>
    /// Synchronous variant for use during entity construction where
    /// the URL is a literal (no DNS resolution needed). Throws on the
    /// first disallowed condition.
    /// </summary>
    public Uri ValidateLiteral(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new UrlSafetyException("URL is empty.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new UrlSafetyException($"URL is not a valid absolute URI: {url}");

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("https" or "http"))
            throw new UrlSafetyException($"Scheme '{scheme}' is not allowed.");
        if (scheme == "http" && !_options.AllowHttp)
            throw new UrlSafetyException("HTTP is not allowed in this environment. Use HTTPS.");

        var host = uri.Host;
        foreach (var blocked in KnownMetadataHosts)
            if (host.Equals(blocked, StringComparison.OrdinalIgnoreCase))
                throw new UrlSafetyException($"Host '{host}' is in the blocklist.");

        if (_options.AdditionalBlockedHosts is { } extra)
            foreach (var blocked in extra)
                if (host.Equals(blocked, StringComparison.OrdinalIgnoreCase))
                    throw new UrlSafetyException($"Host '{host}' is in the operator blocklist.");

        if (IPAddress.TryParse(host, out var literalIp) && IsDisallowedAddress(literalIp))
            throw new UrlSafetyException($"IP literal '{host}' is in a disallowed range.");

        return uri;
    }

    /// <summary>
    /// Returns true if the given IP is in any disallowed range
    /// (loopback, private, link-local, multicast, reserved, etc.).
    /// Honors <see cref="UrlSafetyOptions.AllowPrivateAddresses"/> — when true,
    /// private/loopback ranges pass through but reserved/broadcast/multicast
    /// remain blocked regardless (those are never legitimate targets).
    /// </summary>
    private bool IsDisallowedAddress(IPAddress address)
    {
        // Reserved / non-routable ranges that are NEVER legitimate targets,
        // even when private addresses are otherwise allowed.
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            var b0 = bytes[0];

            // 0.0.0.0/8 — "this network"
            if (b0 == 0) return true;

            // 224.0.0.0/4 (multicast)
            if (b0 >= 224 && b0 <= 239) return true;

            // 240.0.0.0/4 (reserved / broadcast)
            if (b0 >= 240) return true;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ff00::/8 (multicast)
            if (address.IsIPv6Multicast) return true;
        }

        // Operator opt-in: skip private-range checks (loopback, RFC1918,
        // link-local, ULA) when the deployment is allowed to reach them
        // (e.g., local dev with CLIProxyAPI on localhost).
        if (_options.AllowPrivateAddresses) return false;

        // ── Private/loopback/link-local ranges ──
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            var b0 = bytes[0];

            // 10.0.0.0/8 (private)
            if (b0 == 10) return true;

            // 172.16.0.0/12 (private)
            if (b0 == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

            // 192.168.0.0/16 (private)
            if (b0 == 192 && bytes[1] == 168) return true;

            // 169.254.0.0/16 (link-local — includes cloud metadata)
            if (b0 == 169 && bytes[1] == 254) return true;

            // 100.64.0.0/10 (CGNAT)
            if (b0 == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fe80::/10 (link-local)
            if (address.IsIPv6LinkLocal) return true;
            // fc00::/7 (unique local)
            if (address.IsIPv6UniqueLocal) return true;
            // ::/128 (unspecified, deprecated site-local)
            if (address.IsIPv6SiteLocal) return true;
        }

        return false;
    }
}

/// <summary>
/// Operator-tunable options for URL safety validation.
/// </summary>
public sealed class UrlSafetyOptions
{
    /// <summary>Allow plain HTTP (default: false; only enable for local dev).</summary>
    public bool AllowHttp { get; set; }

    /// <summary>
    /// Allow private network addresses (default: false; only enable for tests
    /// that point to localhost services like CLIProxyAPI).
    /// </summary>
    public bool AllowPrivateAddresses { get; set; }

    /// <summary>Operator-supplied additional hostnames to block.</summary>
    public IReadOnlyList<string>? AdditionalBlockedHosts { get; set; }
}

/// <summary>
/// Thrown when a URL fails safety validation.
/// </summary>
public sealed class UrlSafetyException : Exception
{
    public UrlSafetyException(string message) : base(message) { }
}

/// <summary>
/// DNS resolution abstraction — kept as a separate type so tests can supply
/// a static lookup without depending on the real DNS resolver.
/// Implements <see cref="IDisposable"/> to release the internal gate
/// (a <see cref="SemaphoreSlim"/>) when the application shuts down.
/// </summary>
public sealed class DnsResolver : IDisposable
{
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, IReadOnlyList<IPAddress>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public DnsResolver(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveAllAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literal))
            return [literal];

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(host, out var cached))
                return cached;
        }
        finally
        {
            _gate.Release();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        IReadOnlyList<IPAddress> resolved;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cts.Token);
            resolved = addresses ?? [];
        }
        catch (OperationCanceledException)
        {
            throw new UrlSafetyException($"DNS resolution timed out for host '{host}'.");
        }
        catch (SocketException ex)
        {
            throw new UrlSafetyException($"DNS resolution failed for host '{host}': {ex.SocketErrorCode}.");
        }

        await _gate.WaitAsync(ct);
        try
        {
            _cache[host] = resolved;
        }
        finally
        {
            _gate.Release();
        }

        return resolved;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
