using Arkana.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.AI;

/// <summary>
/// Routes requests to the optimal provider (P0 → P1 → P2 chain).
/// Tries OpenCode first, falls back to external providers.
/// </summary>
internal sealed class ModelRouter : IModelRouter
{
    private readonly IEnumerable<IChatCompletionService> _providers;
    private readonly ILogger<ModelRouter> _logger;

    public ModelRouter(IEnumerable<IChatCompletionService> providers, ILogger<ModelRouter> logger)
    {
        _providers = providers.OrderBy(p => p.ProviderName switch
        {
            "OpenCode" => 0,
            "DeepSeek" => 0,  // Same priority as OpenCode — model routing picks based on DB config
            "OpenAI" => 1,
            "Gemini" => 2,
            "CLIProxyAPI" => 3,
            "Anthropic" => 4,
            "Ollama" => 5,
            _ => 99
        });
        _logger = logger;
    }

    public Task<IChatCompletionService> ResolveAsync(string? preferredProvider = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(preferredProvider))
        {
            var preferred = _providers.FirstOrDefault(p =>
                p.ProviderName.Equals(preferredProvider, StringComparison.OrdinalIgnoreCase));

            if (preferred is not null)
            {
                _logger.LogDebug("Resolved preferred provider: {Provider}", preferredProvider);
                return Task.FromResult(preferred);
            }
        }

        // Return P0 (OpenCode) by default
        var primary = _providers.First();
        _logger.LogDebug("Resolved default provider: {Provider} (P0)", primary.ProviderName);
        return Task.FromResult(primary);
    }

    public Task<IReadOnlyList<IChatCompletionService>> GetAllProvidersAsync(CancellationToken ct)
    {
        return Task.FromResult((IReadOnlyList<IChatCompletionService>)_providers.ToList());
    }
}
