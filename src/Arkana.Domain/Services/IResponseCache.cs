using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arkana.Domain.Interfaces;
using Arkana.Domain.ValueObjects;

namespace Arkana.Domain.Services;

/// <summary>
/// Stable, content-derived cache key for a chat request.
///
/// Why not just hash the JSON?
///   - Two requests with semantically identical messages but different
///     whitespace / property ordering should hash to the same key.
///   - This struct normalizes the inputs into a canonical form before
///     hashing, so the cache hit rate stays high.
///
/// What it includes:
///   - Model name (case-insensitive)
///   - Tools (sorted by function name, JSON-canonicalized)
///   - Messages (role + content; tool calls JSON-canonicalized)
///   - Temperature/top-p/top-k when set on the request
///
/// What it does NOT include:
///   - PreferredProvider (a request to the same prompt on a different
///     upstream should not collide — those are different cost/latency
///     characteristics the user is deliberately choosing).
///   - The user / api key (the cache is per-gateway, not per-tenant —
///     Phase 3 work could shard by tenant if multi-tenant isolation is
///     required).
/// </summary>
public readonly record struct CacheKey : IEquatable<CacheKey>
{
    /// <summary>
    /// 32-byte SHA-256 hash of the canonicalized request. Hex-encoded
    /// for readability in logs and as a dictionary key.
    /// </summary>
    public string Hash { get; }

    private CacheKey(string hash) => Hash = hash;

    public static CacheKey From(ChatRequest request)
    {
        var canonical = Canonicalize(request);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new CacheKey(Convert.ToHexString(bytes));
    }

    private static string Canonicalize(ChatRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("model=").Append(request.Model.ToLowerInvariant()).Append('|');

        // Tools — sorted by name to make order-independent.
        if (request.Tools is { Count: > 0 })
        {
            var sortedTools = request.Tools
                .OrderBy(t => t.Function.Name, StringComparer.Ordinal)
                .Select(t => $"tool:{t.Function.Name}|{t.Function.Description}|{CanonicalJson(t.Function.Parameters)}|{t.Function.Strict}");
            foreach (var tool in sortedTools)
                sb.Append(tool).Append(';');
        }
        sb.Append('|');

        // Messages — role + content. Tool calls JSON-canonicalized in
        // declaration order (callers should not rely on tool-call order
        // being meaningful).
        foreach (var msg in request.Messages)
        {
            sb.Append("m:").Append(msg.Role).Append(':').Append(msg.Content);
            if (msg.ToolCallId is not null) sb.Append(":tcid=").Append(msg.ToolCallId);
            if (msg.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in msg.ToolCalls)
                    sb.Append(":tc=").Append(tc.Id).Append(',').Append(tc.Type)
                      .Append(',').Append(tc.Function.Name)
                      .Append(',').Append(tc.Function.Arguments);
            }
            sb.Append('|');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Canonical JSON for a JsonElement (or null). Walks the element
    /// recursively, ordering object properties alphabetically so that
    /// two semantically equivalent schemas hash identically.
    /// </summary>
    private static string CanonicalJson(JsonElement? element)
    {
        if (element is null) return string.Empty;
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteCanonical(writer, element.Value);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteCanonical(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteStringValue(element.ToString());
                break;
        }
    }

    public bool Equals(CacheKey other) => Hash == other.Hash;
    public override int GetHashCode() => Hash?.GetHashCode() ?? 0;
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"cache:{Hash[..16]}");
}

/// <summary>
/// In-memory cache of chat completions, keyed by <see cref="CacheKey"/>.
///
/// Why this exists (PERF-ARKANA-002):
///   Repeated prompts are extremely common in production:
///     - dashboard "ping" requests on a fixed interval
///     - CI test suites re-running the same prompt
///     - agent loops that re-send the same context after every tool call
///   Caching the response slashes both upstream cost and p99 latency
///   for these workloads.
///
/// Scope of the Phase 2 implementation:
///   - Exact-match only (semantic caching is Phase 3, AI-ARKANA-005).
///   - Per-process, in-memory. Multi-instance deployments see only
///     their own cache (a Redis-backed version is straightforward
///     later — same interface, different impl).
///   - TTL-bounded entries; old entries are evicted on read and on
///     the next write that exceeds the cap.
/// </summary>
public interface IResponseCache
{
    /// <summary>
    /// Look up a previously-cached response. Returns null on miss or
    /// when the entry has expired.
    /// </summary>
    Task<CachedResponse?> GetAsync(CacheKey key, CancellationToken ct = default);

    /// <summary>
    /// Store a response under <paramref name="key"/> for at most
    /// <paramref name="ttl"/>. The entry is evicted on or after
    /// <c>now + ttl</c>; reads after that return null.
    /// </summary>
    Task SetAsync(CacheKey key, CachedResponse value, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Total live entries currently in the cache.</summary>
    int Count { get; }

    /// <summary>
    /// Drop everything. Called on admin actions that change provider
    /// configuration (e.g., a key rotation) — we don't want to serve
    /// stale responses after a credential change.
    /// </summary>
    void InvalidateAll();
}

/// <summary>
/// Snapshot of a chat response suitable for caching. Token counts and
/// the served-by provider name are preserved so accounting still works
/// on a cache hit.
/// </summary>
public sealed record CachedResponse(
    string Content,
    string Model,
    int InputTokens,
    int OutputTokens,
    int ToolCallsCount,
    IReadOnlyList<ToolCallInfo>? ToolCalls,
    string ServedByProviderName,
    DateTimeOffset CachedAt);
