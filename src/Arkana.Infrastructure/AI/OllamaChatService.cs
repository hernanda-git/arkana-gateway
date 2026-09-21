using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Ollama OpenAI-compat provider — a SELF-HOSTED, independent upstream.
/// </summary>
/// <remarks>
/// Added 2026-08-06 to give the fallback chain a genuinely independent
/// second link. Before this, every catalog model (including gpt-4o-mini,
/// claude-haiku-3.5 and gemini-2.0-flash-acc1) resolved to the single
/// OpenCode upstream, so a OpenCode quota exhaustion (429 GoUsageLimitError)
/// took the ENTIRE gateway down — the chain had depth 1 and nothing to
/// fall through to.
///
/// Ollama runs as a sibling container on the compose network and requires
/// NO API key and NO OAuth, so it cannot be affected by an upstream
/// account/quota/region failure. It serves the OpenAI-compatible surface
/// at <c>/v1/chat/completions</c>, so it reuses
/// <see cref="Dialects.OpenAiDialectTranslator"/> unchanged.
///
/// The base class already treats an empty API key as "send no
/// Authorization header", which is exactly what Ollama wants.
/// </remarks>
internal sealed class OllamaChatService : OpenAiCompatChatServiceBase
{
    public OllamaChatService(
        HttpClient http,
        IProviderCatalog catalog,
        ICredentialVault vault,
        IDialectTranslator translator)
        : base(http, catalog, vault, translator)
    { }

    public override string ProviderName => "Ollama";
    protected override string ProviderCode => "ollama";

    /// <summary>
    /// Relative path — the concrete base address is supplied by the named
    /// HttpClient in DI (<c>OLLAMA_BASE_URL</c>, default
    /// <c>http://ollama:11434/v1/</c>) so the host can be repointed
    /// per-environment without a recompile.
    /// </summary>
    protected override string Endpoint => "chat/completions";

    /// <summary>Ollama ignores auth entirely; no env var key exists.</summary>
    protected override string? EnvVarApiKey => null;
}
