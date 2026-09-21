using Arkana.Domain.Entities;
using FluentAssertions;

namespace Arkana.Domain.Tests.Entities;

public sealed class ProviderAccountTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Provider = Guid.NewGuid();

    [Fact]
    public void Create_NormalizesCode_AndDefaultsToBrokerManagedPending()
    {
        var account = ProviderAccount.Create(Tenant, Provider, "  Gemini-ACC-A ", " Account A ");
        account.Code.Should().Be("gemini-acc-a");
        account.DisplayName.Should().Be("Account A");
        account.AuthOwnership.Should().Be(ProviderAccountAuthOwnership.BrokerManagedOAuth);
        account.BrokerKind.Should().Be(BrokerKind.CLIProxyAPI);
        account.ConnectionStatus.Should().Be(ProviderAccountStatus.Pending);
        account.IsEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("\\absolute")]
    [InlineData("C:\\tokens")]
    public void Create_RejectsUnsafeAuthDirectoryKey(string key)
        => FluentActions.Invoking(() => ProviderAccount.Create(Tenant, Provider, "account", "Account", authDirectoryKey: key))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void BrokerManagedAccount_RequiresBrokerKind()
        => FluentActions.Invoking(() => ProviderAccount.Create(Tenant, Provider, "account", "Account", brokerKind: null))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void Lifecycle_TracksEnableStatusAndCooldown()
    {
        var now = DateTimeOffset.UtcNow;
        var account = ProviderAccount.Create(Tenant, Provider, "account", "Account");
        account.MarkConnected();
        account.IsHealthy(now).Should().BeTrue();
        account.SetCooldown(now.AddMinutes(5), "quota");
        account.IsHealthy(now).Should().BeFalse();
        account.BeginDrain();
        account.ConnectionStatus.Should().Be(ProviderAccountStatus.Draining);
        account.Disable();
        account.ConnectionStatus.Should().Be(ProviderAccountStatus.Disabled);
        account.Enable();
        account.ConnectionStatus.Should().Be(ProviderAccountStatus.Connected);
    }

    [Fact]
    public void AggregateHasNoTokenPersistenceProperties()
        => typeof(ProviderAccount).GetProperties().Select(x => x.Name).Should().NotContain(x =>
            x.Contains("token", StringComparison.OrdinalIgnoreCase) && !x.Equals(nameof(ProviderAccount.TokenExpiresAt), StringComparison.Ordinal));

    [Fact]
    public void ExplicitStatusTransitionsAreFailClosed()
    {
        var account = ProviderAccount.Create(Tenant, Provider, "account", "Account");
        account.MarkReconnectRequired();
        account.IsHealthy().Should().BeFalse();
        account.MarkOutOfSync();
        account.IsEnabled.Should().BeFalse();
        account.MarkMissingCredential();
        account.ConnectionStatus.Should().Be(ProviderAccountStatus.MissingCredential);
    }
}
