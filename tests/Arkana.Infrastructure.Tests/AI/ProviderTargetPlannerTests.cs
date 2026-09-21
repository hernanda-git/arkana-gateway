using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.AI;
using FluentAssertions;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.AI;

public sealed class ProviderTargetPlannerTests
{
    [Fact]
    public async Task Native_account_resolves_native_route_with_account_identity()
    {
        var tenantId = Guid.NewGuid();
        var provider = AiProvider.Create("Gemini Native", "gemini-acc-native", 1);
        provider.SetAuthMethod(AuthMethod.OAuth, Guid.NewGuid());
        var account = ProviderAccount.Create(
            tenantId, provider.Id, "gemini-acc-native", "Native account",
            ProviderAccountAuthOwnership.GatewayManagedOAuth, brokerKind: null);
        var repository = Substitute.For<IProviderAccountRepository>();
        repository.GetByCodeAsync(tenantId, account.Code, Arg.Any<CancellationToken>()).Returns(account);

        var result = await new ProviderTargetPlanner(repository).ResolveAsync(
            tenantId, provider, "gemini-2.5-pro");

        result.IsResolved.Should().BeTrue();
        result.RouteKind.Should().Be(ProviderRouteKind.NativeGemini);
        result.ProviderId.Should().Be(provider.Id);
        result.ProviderAccountId.Should().Be(account.Id);
        result.AccountCode.Should().Be(account.Code);
    }

    [Fact]
    public async Task Broker_account_resolves_broker_route_with_slot_identity()
    {
        var tenantId = Guid.NewGuid();
        var provider = AiProvider.Create("Gemini Broker", "gemini-acc-broker", 1);
        var account = ProviderAccount.Create(
            tenantId, provider.Id, "gemini-acc-broker", "Broker account",
            ProviderAccountAuthOwnership.BrokerManagedOAuth,
            BrokerKind.CLIProxyAPI, brokerInstanceId: "slot-a");
        var repository = Substitute.For<IProviderAccountRepository>();
        repository.GetByCodeAsync(tenantId, account.Code, Arg.Any<CancellationToken>()).Returns(account);

        var result = await new ProviderTargetPlanner(repository).ResolveAsync(
            tenantId, provider, "gemini-2.5-pro");

        result.IsResolved.Should().BeTrue();
        result.RouteKind.Should().Be(ProviderRouteKind.BrokerManagedGemini);
        result.ProviderAccountId.Should().Be(account.Id);
        result.BrokerInstanceId.Should().Be("slot-a");
    }

    [Fact]
    public async Task Missing_account_metadata_is_rejected_even_when_code_has_gemini_prefix()
    {
        var tenantId = Guid.NewGuid();
        var provider = AiProvider.Create("Unbound Gemini", "gemini-acc-unbound", 1);
        var repository = Substitute.For<IProviderAccountRepository>();

        var result = await new ProviderTargetPlanner(repository).ResolveAsync(
            tenantId, provider, "gemini-2.5-pro");

        result.IsResolved.Should().BeFalse();
        result.RejectionReason.Should().NotBeNullOrWhiteSpace();
        result.RouteKind.Should().Be(ProviderRouteKind.Generic);
    }

    [Fact]
    public async Task Account_from_another_tenant_is_rejected()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var provider = AiProvider.Create("Gemini Native", "gemini-acc-cross", 1);
        provider.SetAuthMethod(AuthMethod.OAuth, Guid.NewGuid());
        var account = ProviderAccount.Create(
            otherTenantId, provider.Id, "gemini-acc-cross", "Other tenant account",
            ProviderAccountAuthOwnership.GatewayManagedOAuth, brokerKind: null);
        var repository = Substitute.For<IProviderAccountRepository>();
        repository.GetByCodeAsync(tenantId, account.Code, Arg.Any<CancellationToken>()).Returns((ProviderAccount?)null);

        var result = await new ProviderTargetPlanner(repository).ResolveAsync(
            tenantId, provider, "gemini-2.5-pro");

        result.IsResolved.Should().BeFalse();
        result.ProviderAccountId.Should().BeNull();
    }
}
