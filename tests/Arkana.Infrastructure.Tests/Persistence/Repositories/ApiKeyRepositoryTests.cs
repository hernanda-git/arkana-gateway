using Arkana.Domain.Entities;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Tests.Persistence.Repositories;

public sealed class ApiKeyRepositoryTests
{
    private static GatewayDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new GatewayDbContext(options);
    }

    private static ApiKey CreateKey(string name = "test-key",
        string keyHash = "abc123hash",
        bool isActive = true,
        DateTimeOffset? expiresAt = null)
    {
        var key = ApiKey.Create(name, keyHash, "arkana-test", expiresAt);
        if (!isActive)
        {
            key.Deactivate();
        }
        return key;
    }

    [Fact]
    public async Task AddAsync_Should_PersistApiKey()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ApiKeyRepository(db);
        var key = CreateKey();

        // Act
        await repo.AddAsync(key, CancellationToken.None);

        // Assert
        var all = await repo.GetAllAsync(CancellationToken.None);
        all.Should().ContainSingle().Which.Should().BeEquivalentTo(key,
            opts => opts.Excluding(k => k.AllowedModels));
    }

    [Fact]
    public async Task GetByKeyHashAsync_Should_ReturnMatchingKey()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ApiKeyRepository(db);
        var key1 = CreateKey(name: "key1", keyHash: "hash1");
        var key2 = CreateKey(name: "key2", keyHash: "hash2");

        await repo.AddAsync(key1, CancellationToken.None);
        await repo.AddAsync(key2, CancellationToken.None);

        // Act
        var result = await repo.GetByKeyHashAsync("hash1", CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("key1");
        result.KeyHash.Should().Be("hash1");
    }

    [Fact]
    public async Task GetByKeyHashAsync_Should_ReturnNull_WhenNotFound()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ApiKeyRepository(db);

        // Act
        var result = await repo.GetByKeyHashAsync("nonexistent", CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_Should_ReturnAllKeys()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ApiKeyRepository(db);

        await repo.AddAsync(CreateKey(name: "key1"), CancellationToken.None);
        await repo.AddAsync(CreateKey(name: "key2"), CancellationToken.None);

        // Act
        var all = await repo.GetAllAsync(CancellationToken.None);

        // Assert
        all.Should().HaveCount(2);
        all.Should().Contain(k => k.Name == "key1");
        all.Should().Contain(k => k.Name == "key2");
    }

    [Fact]
    public async Task GetAllAsync_Should_ReturnEmpty_WhenNoKeys()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ApiKeyRepository(db);

        // Act
        var all = await repo.GetAllAsync(CancellationToken.None);

        // Assert
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_Should_ModifyExistingKey()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ApiKeyRepository(db);
        var key = CreateKey(name: "original");

        await repo.AddAsync(key, CancellationToken.None);

        // Act — deactivate the key
        key.Deactivate();
        await repo.UpdateAsync(key, CancellationToken.None);

        // Assert
        var retrieved = await repo.GetByKeyHashAsync(key.KeyHash, CancellationToken.None);
        retrieved.Should().NotBeNull();
        retrieved!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_Should_ReactivateKey()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ApiKeyRepository(db);
        var key = CreateKey(name: "test", isActive: true);

        await repo.AddAsync(key, CancellationToken.None);
        key.Deactivate();
        await repo.UpdateAsync(key, CancellationToken.None);

        // Act — reactivate
        key.Activate();
        await repo.UpdateAsync(key, CancellationToken.None);

        // Assert
        var retrieved = await repo.GetByKeyHashAsync(key.KeyHash, CancellationToken.None);
        retrieved.Should().NotBeNull();
        retrieved!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetByKeyHashAsync_Should_IncludeAllowedModels()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ApiKeyRepository(db);

        // Create a provider and model for the relationship
        var provider = AiProvider.Create("OpenAI", "openai", 1);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync(CancellationToken.None);

        var model = Model.Create(provider.Id, "GPT-4o", "gpt-4o");
        db.Models.Add(model);
        await db.SaveChangesAsync(CancellationToken.None);

        var key = CreateKey(name: "key-with-models");
        key.AllowedModels.Add(model);
        await repo.AddAsync(key, CancellationToken.None);

        // Act
        var retrieved = await repo.GetByKeyHashAsync(key.KeyHash, CancellationToken.None);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.AllowedModels.Should().ContainSingle().Which.Code.Should().Be("gpt-4o");
    }

    [Fact]
    public async Task GetAllAsync_Should_IncludeAllowedModels()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new ApiKeyRepository(db);

        var provider = AiProvider.Create("OpenAI", "openai", 1);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync(CancellationToken.None);

        var model = Model.Create(provider.Id, "GPT-4o", "gpt-4o");
        db.Models.Add(model);
        await db.SaveChangesAsync(CancellationToken.None);

        var key = CreateKey(name: "key-with-models");
        key.AllowedModels.Add(model);
        await repo.AddAsync(key, CancellationToken.None);

        // Act
        var all = await repo.GetAllAsync(CancellationToken.None);

        // Assert
        all.Should().ContainSingle();
        all[0].AllowedModels.Should().ContainSingle().Which.Code.Should().Be("gpt-4o");
    }
}
