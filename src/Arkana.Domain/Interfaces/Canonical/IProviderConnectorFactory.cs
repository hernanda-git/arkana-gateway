using Arkana.Domain.Entities;

namespace Arkana.Domain.Interfaces.Canonical;

/// <summary>
/// Resolves an <see cref="IChatCompletionService"/> for a given
/// <see cref="AiProvider"/> configuration row. The factory is the seam
/// between the provider catalog (DB-backed, ever-growing) and the set
/// of registered connectors (compile-time fixed).
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 14c). One factory implementation is
/// registered; it consults a code-to-connector map built at DI setup
/// time. New dialects (Anthropic, Gemini, Groq, OpenRouter, etc.) plug
/// in by registering a new connector + translator and adding a line to
/// the map — no further changes to the router, the fallback chain, or
/// the handler.
///
/// The factory is async because future implementations may need to
/// resolve per-provider configuration (e.g. fetch API key from the
/// vault on demand). Today's implementation is a pure dictionary
/// lookup, but the signature leaves room for that growth.
/// </remarks>
public interface IProviderConnectorFactory
{
    /// <summary>
    /// Returns the connector for a given <see cref="AiProvider.Code"/>,
    /// or <c>null</c> when no connector is registered for that code.
    /// A <c>null</c> result means the provider row exists in the catalog
    /// but no connector has been registered for it — a configuration
    /// error to surface, not to silently swallow.
    /// </summary>
    Task<IChatCompletionService?> ResolveAsync(AiProvider provider, CancellationToken ct = default);

    /// <summary>
    /// Returns every registered connector's dialect code, in the order
    /// the factory will try them for fallback. Used by
    /// <c>SendChatHandler</c> to build the fallback chain without going
    /// through the catalog (which is DB-backed and may include providers
    /// without connectors).
    /// </summary>
    IReadOnlyList<string> RegisteredDialectCodes { get; }
}
