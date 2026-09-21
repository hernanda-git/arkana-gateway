using Arkana.Domain.Entities;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Tests.Persistence.Repositories;

public sealed class ModelRepositoryTests
{
    private static GatewayDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new GatewayDbContext(options);
    }

    [Fact]
    public async Task GetAllAsync_Should_ReturnAllModelsOrderedByProviderThenName()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ModelRepository(db);

        var providerA = AiProvider.Create("Provider B", "provider-b", 1);
        var providerB = AiProvider.Create("Provider A", "provider-a", 0);
        db.AiProviders.AddRange(providerA, providerB);
        await db.SaveChangesAsync(CancellationToken.None);

        var model1 = Model.Create(providerA.Id, "Model Z", "model-z");
        var model2 = Model.Create(providerA.Id, "Model A", "model-a");
        var model3 = Model.Create(providerB.Id, "Model B", "model-b");
        db.Models.AddRange(model1, model2, model3);
        await db.SaveChangesAsync(CancellationToken.None);

        // Act
        var all = await repo.GetAllAsync(CancellationToken.None);

        // Assert
        all.Should().HaveCount(3);
        // Ordered by Provider.Name then Model.Name:
        // Provider A (Model B), Provider B (Model A, Model Z)
        all[0].Code.Should().Be("model-b");     // Provider A's model
        all[1].Code.Should().Be("model-a");     // Provider B's model (alphabetically first)
        all[2].Code.Should().Be("model-z");     // Provider B's model
    }

    [Fact]
    public async Task GetAllAsync_Should_IncludeProviderNavigation()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ModelRepository(db);

        var provider = AiProvider.Create("TestProvider", "test-provider", 0);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync(CancellationToken.None);

        var model = Model.Create(provider.Id, "Test Model", "test-model");
        db.Models.Add(model);
        await db.SaveChangesAsync(CancellationToken.None);

        // Act
        var all = await repo.GetAllAsync(CancellationToken.None);

        // Assert
        all.Should().ContainSingle();
        all[0].Provider.Should().NotBeNull();
        all[0].Provider.Name.Should().Be("TestProvider");
    }

    [Fact]
    public async Task GetAllAsync_Should_ReturnEmpty_WhenNoModels()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ModelRepository(db);

        // Act
        var all = await repo.GetAllAsync(CancellationToken.None);

        // Assert
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByProviderIdAsync_Should_ReturnModelsForProvider()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ModelRepository(db);

        var provider1 = AiProvider.Create("Provider1", "provider1", 0);
        var provider2 = AiProvider.Create("Provider2", "provider2", 1);
        db.AiProviders.AddRange(provider1, provider2);
        await db.SaveChangesAsync(CancellationToken.None);

        db.Models.AddRange(
            Model.Create(provider1.Id, "Model A1", "model-a1"),
            Model.Create(provider1.Id, "Model A2", "model-a2"),
            Model.Create(provider2.Id, "Model B1", "model-b1")
        );
        await db.SaveChangesAsync(CancellationToken.None);

        // Act
        var provider1Models = await repo.GetByProviderIdAsync(provider1.Id,
            CancellationToken.None);

        // Assert
        provider1Models.Should().HaveCount(2);
        provider1Models.Should().BeInAscendingOrder(m => m.Name);
        provider1Models.Should().AllSatisfy(m => m.ProviderId.Should().Be(provider1.Id));
    }

    [Fact]
    public async Task GetByProviderIdAsync_Should_ReturnEmpty_WhenNoModelsForProvider()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ModelRepository(db);

        var provider = AiProvider.Create("Provider", "provider", 0);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync(CancellationToken.None);

        // Act
        var models = await repo.GetByProviderIdAsync(provider.Id,
            CancellationToken.None);

        // Assert
        models.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByProviderIdAsync_Should_ReturnEmpty_WhenProviderNotFound()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ModelRepository(db);

        // Act
        var models = await repo.GetByProviderIdAsync(Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        models.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByIdAsync_Should_ReturnModelWithProvider()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ModelRepository(db);

        var provider = AiProvider.Create("TestProvider", "test-provider", 0);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync(CancellationToken.None);

        var model = Model.Create(provider.Id, "Test Model", "test-model");
        db.Models.Add(model);
        await db.SaveChangesAsync(CancellationToken.None);

        // Act
        var result = await repo.GetByIdAsync(model.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Model");
        result.Code.Should().Be("test-model");
        result.Provider.Should().NotBeNull();
        result.Provider.Name.Should().Be("TestProvider");
    }

    [Fact]
    public async Task GetByIdAsync_Should_ReturnNull_WhenNotFound()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ModelRepository(db);

        // Act
        var result = await repo.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_Should_OnlyReturnModelsForGivenProvider()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ModelRepository(db);

        var provider = AiProvider.Create("Provider", "provider", 0);
        var otherProvider = AiProvider.Create("Other", "other", 1);
        db.AiProviders.AddRange(provider, otherProvider);
        await db.SaveChangesAsync(CancellationToken.None);

        db.Models.AddRange(
            Model.Create(provider.Id, "Model 1", "model-1"),
            Model.Create(provider.Id, "Model 2", "model-2"),
            Model.Create(otherProvider.Id, "Other Model", "other-model")
        );
        await db.SaveChangesAsync(CancellationToken.None);

        // Act
        var providerModels = await repo.GetByProviderIdAsync(provider.Id,
            CancellationToken.None);

        // Assert
        providerModels.Should().HaveCount(2);
        providerModels.Should().AllSatisfy(m => m.ProviderId.Should().Be(provider.Id));
    }
}
