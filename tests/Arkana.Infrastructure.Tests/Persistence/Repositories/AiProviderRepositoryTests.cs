using Arkana.Domain.Entities;
using Arkana.Domain.Services;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Repositories;
using Arkana.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Tests.Persistence.Repositories;

public sealed class AiProviderRepositoryTests
{
    // 32-byte test key for the envelope vault used in tests.
    private static readonly byte[] TestMasterKey = new byte[]
    {
        0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10,
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00
    };
    private static EnvelopeCredentialVault TestVault() => new(TestMasterKey);

    private static GatewayDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new GatewayDbContext(options);
    }

    [Fact]
    public async Task GetAllAsync_Should_ReturnAllProvidersOrderedByPriorityThenName()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new AiProviderRepository(db);

        var provider1 = AiProvider.Create("Provider B", "provider-b", priority: 2);
        var provider2 = AiProvider.Create("Provider A", "provider-a", priority: 1);
        var provider3 = AiProvider.Create("Provider C", "provider-c", priority: 1);

        db.AiProviders.AddRange(provider1, provider2, provider3);
        await db.SaveChangesAsync(CancellationToken.None);

        // Act
        var all = await repo.GetAllAsync(CancellationToken.None);

        // Assert
        all.Should().HaveCount(3);
        // Ordered by Priority ASC, then Name ASC:
        // Priority 1: Provider A, Provider C
        // Priority 2: Provider B
        all[0].Code.Should().Be("provider-a");
        all[1].Code.Should().Be("provider-c");
        all[2].Code.Should().Be("provider-b");
    }

    [Fact]
    public async Task GetAllAsync_Should_ReturnEmpty_WhenNoProviders()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new AiProviderRepository(db);

        // Act
        var all = await repo.GetAllAsync(CancellationToken.None);

        // Assert
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByIdAsync_Should_ReturnMatchingProvider()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new AiProviderRepository(db);

        var provider = AiProvider.Create("TestProvider", "test-provider", 0,
            "http://localhost:8080");
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync(CancellationToken.None);

        // Act
        var result = await repo.GetByIdAsync(provider.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("TestProvider");
        result.Code.Should().Be("test-provider");
        result.BaseUrl.Should().Be("http://localhost:8080");
    }

    [Fact]
    public async Task GetByIdAsync_Should_ReturnNull_WhenNotFound()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new AiProviderRepository(db);

        // Act
        var result = await repo.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_Should_ModifyExistingProvider()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new AiProviderRepository(db);

        var provider = AiProvider.Create("TestProvider", "test-provider", 0);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync(CancellationToken.None);

        // Act — disable the provider
        provider.Disable();
        await repo.UpdateAsync(provider, CancellationToken.None);

        // Assert
        var retrieved = await repo.GetByIdAsync(provider.Id, CancellationToken.None);
        retrieved.Should().NotBeNull();
        retrieved!.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_Should_UpdateCredentials()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new AiProviderRepository(db);
        var vault = TestVault();

        // Allow-all validator so the test exercises the credential-update path
        // without tripping the SSRF guard.
        var validator = new UrlSafetyValidator(
            new UrlSafetyOptions { AllowHttp = true, AllowPrivateAddresses = true },
            new DnsResolver(TimeSpan.FromMilliseconds(100)));

        var provider = AiProvider.Create("TestProvider", "test-provider", 0,
            validator: validator);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync(CancellationToken.None);

        // Act — update credentials (validator + vault both required after merge)
        provider.UpdateCredentials("https://new-url.com", "new-api-key", vault, validator);
        await repo.UpdateAsync(provider, CancellationToken.None);

        // Assert
        var retrieved = await repo.GetByIdAsync(provider.Id, CancellationToken.None);
        retrieved.Should().NotBeNull();
        retrieved!.BaseUrl.Should().Be("https://new-url.com");
        // The stored ApiKey is the sealed form — not plaintext.
        retrieved.ApiKey.Should().NotBe("new-api-key");
        retrieved.ApiKey.Should().StartWith("v1:");
        // Decryption with the same vault yields the original plaintext.
        retrieved.DecryptApiKey(vault).Should().Be("new-api-key");
    }

    [Fact]
    public async Task UpdateAsync_Should_ReactivateProvider()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new AiProviderRepository(db);

        var provider = AiProvider.Create("TestProvider", "test-provider", 0);
        db.AiProviders.Add(provider);
        await db.SaveChangesAsync(CancellationToken.None);

        provider.Disable();
        await repo.UpdateAsync(provider, CancellationToken.None);

        // Act — re-enable
        provider.Enable();
        await repo.UpdateAsync(provider, CancellationToken.None);

        // Assert
        var retrieved = await repo.GetByIdAsync(provider.Id, CancellationToken.None);
        retrieved.Should().NotBeNull();
        retrieved!.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task GetAllAsync_Should_IncludeDisabledProviders()
    {
        // Arrange
        using var db = CreateDbContext();
        var repo = new AiProviderRepository(db);

        var provider1 = AiProvider.Create("Provider1", "provider1", 0);
        var provider2 = AiProvider.Create("Provider2", "provider2", 1);
        provider2.Disable();

        db.AiProviders.AddRange(provider1, provider2);
        await db.SaveChangesAsync(CancellationToken.None);

        // Act
        var all = await repo.GetAllAsync(CancellationToken.None);

        // Assert
        all.Should().HaveCount(2);
        all.Should().Contain(p => p.IsEnabled);
        all.Should().Contain(p => !p.IsEnabled);
    }

    [Fact]
    public async Task DeleteAsync_WithTenantId_ShouldNotDeleteAnotherTenantsProvider()
    {
        using var db = CreateDbContext();
        var repo = new AiProviderRepository(db);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var providerA = AiProvider.Create("Provider A", "provider-a", 0);
        var providerB = AiProvider.Create("Provider B", "provider-b", 0);
        providerA.AssignTenant(tenantA);
        providerB.AssignTenant(tenantB);
        db.AiProviders.AddRange(providerA, providerB);
        await db.SaveChangesAsync(CancellationToken.None);

        await repo.DeleteAsync(providerA.Id, tenantB, CancellationToken.None);

        (await repo.GetByIdAsync(providerA.Id, CancellationToken.None)).Should().NotBeNull();
        await repo.DeleteAsync(providerA.Id, tenantA, CancellationToken.None);
        (await repo.GetByIdAsync(providerA.Id, CancellationToken.None)).Should().BeNull();
        (await repo.GetByIdAsync(providerB.Id, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task MultipleProviders_Should_BeIsolatedByDbContext()
    {
        // Arrange
        using var db1 = CreateDbContext();
        using var db2 = CreateDbContext();
        var repo1 = new AiProviderRepository(db1);
        var repo2 = new AiProviderRepository(db2);

        var providerA = AiProvider.Create("Provider A", "provider-a", 0);
        var providerB = AiProvider.Create("Provider B", "provider-b", 0);

        db1.AiProviders.Add(providerA);
        db2.AiProviders.Add(providerB);
        await db1.SaveChangesAsync(CancellationToken.None);
        await db2.SaveChangesAsync(CancellationToken.None);

        // Act
        var all1 = await repo1.GetAllAsync(CancellationToken.None);
        var all2 = await repo2.GetAllAsync(CancellationToken.None);

        // Assert
        all1.Should().ContainSingle().Which.Name.Should().Be("Provider A");
        all2.Should().ContainSingle().Which.Name.Should().Be("Provider B");
    }
}
