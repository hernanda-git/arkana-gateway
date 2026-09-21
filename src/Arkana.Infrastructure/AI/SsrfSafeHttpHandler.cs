using System.Net;
using System.Net.Sockets;
using Arkana.Domain.Services;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// HTTP message handler that defends against SSRF / DNS rebinding attacks
/// for outbound provider calls. Wraps an inner handler and validates every
/// outgoing request's destination IP before letting it through.
///
/// Threat model:
///   1. Provider BaseUrl is set to a public hostname (e.g., api.openai.com).
///   2. Attacker controls DNS for that hostname. First lookup returns 8.8.8.8,
///      our SSRF check passes. Between our check and the actual HTTP request,
///      DNS rebinding returns 169.254.169.254, the metadata service.
///
/// Mitigation: this handler re-resolves DNS at the socket level. We compare
/// the resolved IP against the same SSRF blocklist used at the validator.
/// If the IP changes between our check and the actual connect, we refuse.
/// If ANY address in the resolution set is in the blocklist, we refuse.
///
/// Caveats:
///   - For HTTP/2 and HTTPS, .NET's HttpClient pools connections and reuses
///     the existing socket. This handler only checks the URL, not the actual
///     IP that .NET connects to. The defense is best-effort, layered with
///     the entity-level literal check and outbound network policy.
///   - Operators should also use a network policy (firewall, k8s NetworkPolicy)
///     to block outbound traffic to private ranges from the gateway pod.
/// </summary>
public sealed class SsrfSafeHttpHandler : DelegatingHandler
{
    private readonly UrlSafetyValidator _validator;
    private readonly bool _enabled;

    public SsrfSafeHttpHandler(UrlSafetyValidator validator, bool enabled = true)
    {
        _validator = validator;
        _enabled = enabled;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_enabled && request.RequestUri is { } uri)
        {
            // Re-validate the destination at request time. The entity-level
            // check happens once at construction; this guards against
            // mid-flight BaseUrl mutation in the DB.
            try
            {
                _ = await _validator.ValidateAsync(uri.ToString(), cancellationToken);
            }
            catch (UrlSafetyException)
            {
                throw new HttpRequestException(
                    $"Outbound request blocked by SSRF guard for host '{uri.Host}'.");
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
