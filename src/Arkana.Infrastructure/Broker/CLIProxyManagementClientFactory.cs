using Microsoft.Extensions.Options;

namespace Arkana.Infrastructure.Broker;

public sealed class CLIProxyManagementClientFactory : ICLIProxyManagementClientFactory
{
    private readonly IHttpClientFactory _http;
    private readonly CLIProxyManagementOptions _options;
    public CLIProxyManagementClientFactory(IHttpClientFactory http, IOptions<CLIProxyManagementOptions> options)
    { _http = http; _options = options.Value; }
    public ICLIProxyManagementClient Create(string slot)
    {
        if (string.IsNullOrWhiteSpace(slot) || !_options.Slots.TryGetValue(slot, out var cfg))
            throw new ArgumentException("Broker slot is not allowlisted.", nameof(slot));
        if (!Uri.TryCreate(cfg.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(cfg.ManagementKey))
            throw new InvalidOperationException($"Broker slot '{slot}' is not safely configured.");
        var client = _http.CreateClient("gemini-broker-management");
        client.BaseAddress = new Uri(uri, uri.AbsoluteUri.EndsWith('/') ? string.Empty : "/");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.ManagementKey);
        return new CLIProxyManagementClient(client, _options, slot);
    }
}
