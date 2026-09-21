using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using MediatR;

namespace Arkana.Application.Features.Admin.Commands;

/// <summary>
/// Register a new API key for gateway access.
/// </summary>
public sealed record CreateApiKeyCommand : IRequest<CreateApiKeyResult>
{
    public string Name { get; init; } = string.Empty;
    public List<Guid> AllowedModelIds { get; init; } = [];
    public string PreferredProviderCode { get; init; } = string.Empty;
    public bool AllowProviderFallback { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Result of a successful API key creation.
/// </summary>
public sealed record CreateApiKeyResult
{
    public Guid Id { get; init; }
    public string PlainTextKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

internal sealed class CreateApiKeyHandler : IRequestHandler<CreateApiKeyCommand, CreateApiKeyResult>
{
    private readonly IApiKeyRepository _repo;
    private readonly IModelRepository _models;
    private readonly IAiProviderRepository _providers;

    public CreateApiKeyHandler(IApiKeyRepository repo, IModelRepository models,
        IAiProviderRepository providers)
    {
        _repo = repo;
        _models = models;
        _providers = providers;
    }

    public async Task<CreateApiKeyResult> Handle(CreateApiKeyCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.PreferredProviderCode))
            throw new InvalidOperationException("A provider pin is required.");
        if (command.AllowedModelIds.Count == 0)
            throw new InvalidOperationException("At least one allowed model is required.");

        var providers = await _providers.GetAllAsync(ct);
        var provider = providers.FirstOrDefault(p => p.IsEnabled
            && p.Code.Equals(command.PreferredProviderCode.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Provider '{command.PreferredProviderCode}' is unavailable.");
        var allModels = await _models.GetAllAsync(ct);
        var selectedModels = allModels.Where(m => command.AllowedModelIds.Contains(m.Id)).ToList();
        if (selectedModels.Count != command.AllowedModelIds.Distinct().Count())
            throw new InvalidOperationException("One or more selected models do not exist.");
        var mismatched = selectedModels.FirstOrDefault(m => m.ProviderId != provider.Id);
        if (mismatched is not null)
            throw new InvalidOperationException(
                $"Model '{mismatched.Code}' does not belong to provider '{provider.Code}'.");

        var plainKey = Domain.Services.ApiKeyHasher.GenerateApiKey();
        var hash = Domain.Services.ApiKeyHasher.Hash(plainKey);

        var apiKey = ApiKey.Create(command.Name, hash,
            Domain.Services.ApiKeyHasher.ExtractPrefix(plainKey), command.ExpiresAt);
        apiKey.PreferredProviderCode = provider.Code;
        apiKey.AllowProviderFallback = command.AllowProviderFallback;

        // Attach selected models
        if (command.AllowedModelIds.Count > 0)
        {
            foreach (var modelId in command.AllowedModelIds)
            {
                var model = allModels.FirstOrDefault(m => m.Id == modelId);
                if (model is not null)
                    apiKey.AllowedModels.Add(model);
            }
        }

        await _repo.AddAsync(apiKey, ct);

        return new CreateApiKeyResult
        {
            Id = apiKey.Id,
            PlainTextKey = plainKey,
            Name = apiKey.Name
        };
    }
}
