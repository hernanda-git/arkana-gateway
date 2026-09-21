using Microsoft.AspNetCore.Http;

namespace Arkana.Gateway.Api.Middleware;

/// <summary>
/// Single source of truth for pulling a caller's credential off an inbound
/// request.
/// </summary>
/// <remarks>
/// <para>
/// This logic used to be copy-pasted in three places
/// (<c>ApiKeyAuthMiddleware</c>, <c>ChatEndpoints</c>, <c>ResponsesEndpoints</c>)
/// in this form:
/// </para>
/// <code>
/// Headers["X-Api-Key"].FirstOrDefault()
///   ?? Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "")
/// </code>
/// <para>
/// which has two real defects:
/// </para>
/// <list type="number">
///   <item><description>
///     <see cref="string.Replace(string, string)"/> is not a prefix strip. It
///     removes <em>every</em> occurrence anywhere in the value, so a key that
///     happens to contain the substring <c>"Bearer "</c> is silently corrupted
///     and then fails to match its stored hash.
///   </description></item>
///   <item><description>
///     The match is case-sensitive, but RFC 6750 makes the scheme token
///     case-insensitive. A client sending <c>authorization: bearer sk-…</c> —
///     legal, and what some HTTP/2 clients emit — is not stripped at all, so
///     the literal text <c>"bearer sk-…"</c> is hashed and the caller gets a
///     baffling 401 with a credential that is actually correct.
///   </description></item>
/// </list>
/// <para>
/// Both are fixed here once. Prefer <see cref="TryExtract"/> over re-reading
/// headers; downstream of <c>ApiKeyAuthMiddleware</c> prefer
/// <c>HttpContext.Items["ApiKeyId"]</c>, which is already resolved and avoids
/// hashing the same key twice per request.
/// </para>
/// </remarks>
public static class ApiKeyExtractor
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Extracts the caller's API key from <c>X-Api-Key</c>, falling back to a
    /// bearer token in <c>Authorization</c>.
    /// </summary>
    /// <returns>True when a non-empty credential was found.</returns>
    public static bool TryExtract(HttpRequest request, out string apiKey)
    {
        var direct = request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(direct))
        {
            apiKey = direct.Trim();
            return true;
        }

        return TryExtractBearer(request, out apiKey);
    }

    /// <summary>
    /// Extracts a bearer token from the <c>Authorization</c> header, matching
    /// the scheme case-insensitively and stripping it as a true prefix.
    /// </summary>
    /// <returns>True when a non-empty bearer token was found.</returns>
    public static bool TryExtractBearer(HttpRequest request, out string token)
    {
        token = string.Empty;

        var header = request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header)) return false;

        var span = header.AsSpan().Trim();
        if (!span.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var value = span[BearerPrefix.Length..].Trim();
        if (value.IsEmpty) return false;

        token = value.ToString();
        return true;
    }

    /// <summary>
    /// Convenience overload returning null instead of a bool + out parameter.
    /// </summary>
    public static string? Extract(HttpRequest request) =>
        TryExtract(request, out var key) ? key : null;
}
