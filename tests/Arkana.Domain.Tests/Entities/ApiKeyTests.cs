using Arkana.Domain.Entities;
using FluentAssertions;

namespace Arkana.Domain.Tests.Entities;

public sealed class ApiKeyTests
{
    [Fact]
    public void NewKeys_DefaultToPoolWithoutEitherFallbackPermission()
    {
        var key = ApiKey.Create("Key", "hash", "arkana-key");

        key.AccountRoutingMode.Should().Be(AccountRoutingMode.Pool);
        key.PreferredProviderAccountId.Should().BeNull();
        key.AllowAccountFallback.Should().BeFalse();
        key.AllowProviderFallback.Should().BeFalse();
    }

    [Fact]
    public void AccountPin_IsIndependentFromProviderFallback()
    {
        var key = ApiKey.Create("Key", "hash", "arkana-key");
        var accountId = Guid.NewGuid();
        key.PreferredProviderAccountId = accountId;
        key.AccountRoutingMode = AccountRoutingMode.StrictPin;

        key.PreferredProviderAccountId.Should().Be(accountId);
        key.AccountRoutingMode.Should().Be(AccountRoutingMode.StrictPin);
        key.AllowAccountFallback.Should().BeFalse();
        key.AllowProviderFallback.Should().BeFalse();
    }

    [Fact]
    public void Create_SetsAllPropertiesCorrectly()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30);
        var key = ApiKey.Create("Test Key", "abc123hash", "arkana-test", expiresAt);

        key.Name.Should().Be("Test Key");
        key.KeyHash.Should().Be("abc123hash");
        key.ExpiresAt.Should().Be(expiresAt);
        key.IsActive.Should().BeTrue();
        key.Id.Should().NotBeEmpty();
        key.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void IsExpired_ReturnsTrueWhenExpirationIsInThePast()
    {
        var key = ApiKey.Create("Expired Key", "hash", "arkana-expired", DateTimeOffset.UtcNow.AddDays(-1));

        key.IsExpired().Should().BeTrue();
    }

    [Fact]
    public void IsExpired_ReturnsFalseWhenNoExpirationSet()
    {
        var key = ApiKey.Create("No Expiry Key", "hash", "arkana-noexp");

        key.IsExpired().Should().BeFalse();
    }

    [Fact]
    public void Deactivate_SetsIsActiveToFalse()
    {
        var key = ApiKey.Create("Key", "hash", "arkana-key");

        key.Deactivate();

        key.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Activate_SetsIsActiveToTrue()
    {
        var key = ApiKey.Create("Key", "hash", "arkana-key");
        key.Deactivate();

        key.Activate();

        key.IsActive.Should().BeTrue();
    }

    [Fact]
    public void CanAccessModel_ReturnsTrueWhenAllowedModelsIsEmpty()
    {
        var key = ApiKey.Create("Key", "hash", "arkana-key");

        key.CanAccessModel(Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void CanAccessModel_ReturnsTrueWhenModelIsInAllowedModels()
    {
        var key = ApiKey.Create("Key", "hash", "arkana-key");
        var model = Model.Create(Guid.NewGuid(), "Test Model", "test");
        key.AllowedModels.Add(model);

        key.CanAccessModel(model.Id).Should().BeTrue();
    }

    [Fact]
    public void CanAccessModel_ReturnsFalseWhenModelIsNotInAllowedModels()
    {
        var key = ApiKey.Create("Key", "hash", "arkana-key");
        var model = Model.Create(Guid.NewGuid(), "Test Model", "test");
        key.AllowedModels.Add(model);

        key.CanAccessModel(Guid.NewGuid()).Should().BeFalse();
    }
}
