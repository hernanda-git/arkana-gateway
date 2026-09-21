namespace Arkana.Domain.Interfaces;

/// <summary>
/// Routes requests to the optimal AI provider based on cost, capability, and priority.
/// </summary>
public interface IModelRouter
{
    Task<IChatCompletionService> ResolveAsync(string? preferredProvider = null, CancellationToken ct = default);
    Task<IReadOnlyList<IChatCompletionService>> GetAllProvidersAsync(CancellationToken ct = default);
}
