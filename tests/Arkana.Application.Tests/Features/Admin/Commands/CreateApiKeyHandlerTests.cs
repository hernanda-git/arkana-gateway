using Arkana.Application.Features.Admin.Commands;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using FluentAssertions;
using NSubstitute;

namespace Arkana.Application.Tests.Features.Admin.Commands;

public class CreateApiKeyHandlerTests
{
    private readonly IApiKeyRepository _repo = Substitute.For<IApiKeyRepository>();
    private readonly IModelRepository _models = Substitute.For<IModelRepository>();
    private readonly IAiProviderRepository _providers = Substitute.For<IAiProviderRepository>();
    private readonly CreateApiKeyHandler _sut;

    public CreateApiKeyHandlerTests()
    {
        _sut = new CreateApiKeyHandler(_repo, _models, _providers);
    }

    [Fact]
    public async Task Handle_WithoutProviderPin_RejectsUnmanagedKey()
    {
        // Arrange
        var command = new CreateApiKeyCommand
        {
            Name = "Test Key",
            AllowedModelIds = [],
            ExpiresAt = null
        };

        var act = () => _sut.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*provider pin is required*");
        await _repo.DidNotReceive().AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithAllowedModels_CreatesApiKeyWithModelRestrictions()
    {
        // Arrange
        var modelId1 = Guid.NewGuid();
        var modelId2 = Guid.NewGuid();

        var providerId = Guid.NewGuid();
        var provider = AiProvider.Create("Codex", "chatgpt-acc1", 1);
        typeof(AiProvider).GetProperty("Id")!.SetValue(provider, providerId);
        _providers.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<AiProvider> { provider });
        var model1 = Model.Create(providerId, "GPT-4", "gpt-4");
        typeof(Model).GetProperty("Id")!.SetValue(model1, modelId1);
        var model2 = Model.Create(providerId, "Codex", "gpt-codex");
        typeof(Model).GetProperty("Id")!.SetValue(model2, modelId2);

        _models.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { model1, model2 });

        var command = new CreateApiKeyCommand
        {
            Name = "Restricted Key",
            AllowedModelIds = [modelId1, modelId2],
            PreferredProviderCode = "chatgpt-acc1",
            AllowProviderFallback = false,
            ExpiresAt = null
        };

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Name.Should().Be("Restricted Key");
        result.Id.Should().NotBeEmpty();
        result.PlainTextKey.Should().NotBeEmpty();

        await _repo.Received(1).AddAsync(
            Arg.Is<ApiKey>(k =>
                k.Name == "Restricted Key" &&
                k.AllowedModels.Count == 2 &&
                k.PreferredProviderCode == "chatgpt-acc1" &&
                !k.AllowProviderFallback),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithNonExistentModelIds_RejectsAmbiguousScope()
    {
        // Arrange
        var existingModelId = Guid.NewGuid();
        var nonExistentId = Guid.NewGuid();

        var providerId = Guid.NewGuid();
        var provider = AiProvider.Create("Codex", "chatgpt-acc1", 1);
        typeof(AiProvider).GetProperty("Id")!.SetValue(provider, providerId);
        _providers.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<AiProvider> { provider });
        var existingModel = Model.Create(providerId, "GPT-4", "gpt-4");
        typeof(Model).GetProperty("Id")!.SetValue(existingModel, existingModelId);

        // Only one model exists in the repository
        _models.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Model> { existingModel });

        var command = new CreateApiKeyCommand
        {
            Name = "Selective Key",
            AllowedModelIds = [existingModelId, nonExistentId], // one exists, one doesn't
            PreferredProviderCode = "chatgpt-acc1",
            ExpiresAt = null
        };

        var act = () => _sut.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*do not exist*");
        await _repo.DidNotReceive().AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }
}
