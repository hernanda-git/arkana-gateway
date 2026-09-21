using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Cloudflare Workers AI OpenAI-compat provider. Endpoints at
/// <c>https://api.cloudflare.com/client/v4/accounts/{account_id}/ai/v1</c>.
/// The <c>account_id</c> segment is a per-tenant path component; for
/// the gateway's purposes we ship the prefix and let the DB store the
/// per-tenant account_id via a future enhancement, or rely on the
/// environment variable <c>CLOUDFLARE_ACCOUNT_ID</c>.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 16). Cloudflare's OpenAI-compat endpoint
/// requires an account_id path component. For a single-tenant gateway
/// deployment, setting <c>CLOUDFLARE_ACCOUNT_ID</c> covers it; for
/// multi-tenant, the AiProvider row's BaseUrl field should override.
/// </remarks>
internal sealed class CloudflareChatService : OpenAiCompatChatServiceBase
{
    public CloudflareChatService(
        HttpClient http,
        IProviderCatalog catalog,
        ICredentialVault vault,
        IDialectTranslator translator)
        : base(http, catalog, vault, translator)
    { }

    public override string ProviderName => "Cloudflare";
    protected override string ProviderCode => "cloudflare";
    protected override string EnvVarApiKey => "CLOUDFLARE_API_KEY";

    protected override string Endpoint
    {
        get
        {
            // Cloudflare's OpenAI-compat path is templated on account_id.
            // The default below assumes the gateway admin has set
            // CLOUDFLARE_ACCOUNT_ID; operators who need per-tenant URLs
            // can override via the AiProvider row's BaseUrl.
            var accountId = Environment.GetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID")
                ?? "default";
            return $"https://api.cloudflare.com/client/v4/accounts/{accountId}/ai/v1/chat/completions";
        }
    }
}
