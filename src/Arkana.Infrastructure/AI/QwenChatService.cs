using Arkana.Domain.Interfaces;
using Arkana.Domain.Interfaces.Canonical;
using Arkana.Domain.Services;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Alibaba Qwen (DashScope) OpenAI-compat provider. Endpoints at
/// <c>https://dashscope.aliyuncs.com/compatible-mode/v1</c>.
/// </summary>
/// <remarks>
/// AI-ARKANA-004 (Phase 3, task 16). Qwen's OpenAI-compat mode is at
/// the <c>compatible-mode</c> path; the base URL is the Alibaba Cloud
/// endpoint, not the international one.
/// </remarks>
internal sealed class QwenChatService : OpenAiCompatChatServiceBase
{
    public QwenChatService(
        HttpClient http,
        IProviderCatalog catalog,
        ICredentialVault vault,
        IDialectTranslator translator)
        : base(http, catalog, vault, translator)
    { }

    public override string ProviderName => "Qwen";
    protected override string ProviderCode => "qwen";
    protected override string Endpoint => "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";
    protected override string? EnvVarApiKey => "QWEN_API_KEY";
}
