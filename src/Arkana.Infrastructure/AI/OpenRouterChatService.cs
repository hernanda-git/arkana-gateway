using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// OpenRouter OpenAI-compat aggregator. Routes requests to many
/// upstream providers through a single OpenAI-shape API. Endpoints at
/// <c>https://openrouter.ai/api/v1</c>.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 16). Uses StringContent for the body
/// (the OpenRouter upstream is sensitive to Content-Type nuances in
/// some proxies).
/// </remarks>
internal sealed class OpenRouterChatService : OpenAiCompatChatServiceBase
{
    public OpenRouterChatService(
        HttpClient http,
        IProviderCatalog catalog,
        ICredentialVault vault,
        IDialectTranslator translator)
        : base(http, catalog, vault, translator)
    { }

    public override string ProviderName => "OpenRouter";
    protected override string ProviderCode => "openrouter";
    protected override string Endpoint => "https://openrouter.ai/api/v1/chat/completions";
    protected override string? EnvVarApiKey => "OPENROUTER_API_KEY";
    protected override bool UseTypedJsonContent => false;
}
