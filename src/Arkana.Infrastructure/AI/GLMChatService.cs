using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Zhipu GLM (BigModel) OpenAI-compat provider. Endpoints at
/// <c>https://open.bigmodel.cn/api/paas/v4</c>.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 16). GLM is a major Chinese provider;
/// the OpenAI-compat path lives under <c>/api/paas/v4</c>.
/// </remarks>
internal sealed class GLMChatService : OpenAiCompatChatServiceBase
{
    public GLMChatService(
        HttpClient http,
        IProviderCatalog catalog,
        ICredentialVault vault,
        IDialectTranslator translator)
        : base(http, catalog, vault, translator)
    { }

    public override string ProviderName => "GLM";
    protected override string ProviderCode => "glm";
    protected override string Endpoint => "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    protected override string? EnvVarApiKey => "GLM_API_KEY";
}
