using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Groq OpenAI-compat provider. Endpoints at <c>https://api.groq.com/openai/v1</c>.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 16). One of five new OpenAI-compat providers
/// plugged in via the dialect abstraction. Reuses
/// <see cref="Dialects.OpenAiDialectTranslator"/>; this class exists only
/// to set the four config values.
/// </remarks>
internal sealed class GroqChatService : OpenAiCompatChatServiceBase
{
    public GroqChatService(
        HttpClient http,
        IProviderCatalog catalog,
        ICredentialVault vault,
        IDialectTranslator translator)
        : base(http, catalog, vault, translator)
    { }

    public override string ProviderName => "Groq";
    protected override string ProviderCode => "groq";
    protected override string Endpoint => "https://api.groq.com/openai/v1/chat/completions";
    protected override string? EnvVarApiKey => "GROQ_API_KEY";
}
