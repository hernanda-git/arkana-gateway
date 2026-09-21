using System.Reflection;
using System.Text;
using Arkana.Application.Features.Chat;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.AI;
using Arkana.Infrastructure.Broker;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Arkana.Infrastructure.Tests.AI;

public sealed class GeminiSubscriptionStreamingRoutingTests
{
    [Fact]
    public async Task Streaming_completion_uses_broker_stream_and_preserves_account_attribution()
    {
        var tenantId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var account = ProviderAccount.Create(tenantId, providerId, "gemini-acc1", "Gemini account", brokerInstanceId: "gateway-host");
        account.MarkConnected();

        var selector = Substitute.For<IProviderAccountSelector>();
        selector.SelectAsync(Arg.Any<ProviderAccountSelectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProviderAccountSelectionResult(account, [account], null));

        var repository = Substitute.For<IProviderAccountRepository>();
        repository.GetByIdNoTrackingAsync(tenantId, account.Id, Arg.Any<CancellationToken>()).Returns(account);
        repository.AcquireMutationLeaseAsync(tenantId, account.Id, Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IAsyncDisposable>());
        repository.TryReserveVersionAsync(tenantId, account.Id, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                SetVersion(account, callInfo.ArgAt<Guid>(3));
                return true;
            });

        var dataPlane = Substitute.For<IGeminiSubscriptionDataPlaneClient>();
        var body = new MemoryStream(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
        dataPlane.StreamAsync(Arg.Any<GeminiBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GeminiBrokerStreamResponse(
                response: null,
                stream: body,
                result: new ChatResult { Model = "gemini-2.5-pro", RouteKind = "broker-managed" },
                attempt: AccountAttemptResult.Success()));

        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);
        var options = Options.Create(new GeminiSubscriptionOptions { ProviderId = providerId });
        var service = new GeminiSubscriptionChatService(selector, repository, tenant, dataPlane, options);

        var result = await service.CompleteStreamingAsync(new ChatRequest
        {
            Model = "gemini-2.5-pro",
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
            PreferredProviderCode = "gemini-subscription",
        });

        result.IsSuccess.Should().BeTrue();
        result.Result.ResolvedProviderAccountId.Should().Be(account.Id);
        result.Result.ResolvedProviderAccountCode.Should().Be("gemini-acc1");
        result.Result.RouteKind.Should().Be("broker-managed");
        await result.DisposeAsync();

        dataPlane.ReceivedCalls().Should().NotContain(call =>
            call.GetMethodInfo().Name == nameof(IGeminiSubscriptionDataPlaneClient.CompleteAsync));
        await dataPlane.Received(1).StreamAsync(Arg.Is<GeminiBrokerRequest>(request =>
            request.Slot == "gateway-host" && request.AccountCode == "gemini-acc1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Streaming_reservation_retries_once_with_fresh_snapshot_when_the_first_attempt_loses()
    {
        var tenantId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var pinned = ProviderAccount.Create(tenantId, providerId, "gemini-acc9", "acc9", brokerInstanceId: "gateway-host");
        pinned.MarkConnected();
        var first = ProviderAccount.Create(tenantId, providerId, "gemini-acc9", "acc9", brokerInstanceId: "gateway-host");
        first.MarkConnected();
        var second = ProviderAccount.Create(tenantId, providerId, "gemini-acc9", "acc9", brokerInstanceId: "gateway-host");
        second.MarkConnected();
        var firstVersion = first.Version;
        var secondVersion = second.Version;

        var selector = Substitute.For<IProviderAccountSelector>();
        selector.SelectAsync(Arg.Any<ProviderAccountSelectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProviderAccountSelectionResult(pinned, [pinned], null));

        var attempts = 0;
        var reservedWith = new List<Guid>();
        var repository = Substitute.For<IProviderAccountRepository>();
        repository.AcquireMutationLeaseAsync(tenantId, pinned.Id, Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IAsyncDisposable>());
        repository.GetByIdNoTrackingAsync(tenantId, pinned.Id, Arg.Any<CancellationToken>())
            .Returns(first, second, second);
        repository.TryReserveVersionAsync(tenantId, pinned.Id, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                attempts++;
                reservedWith.Add(callInfo.ArgAt<Guid>(2));
                if (attempts == 1)
                    return false;
                SetVersion(second, callInfo.ArgAt<Guid>(3));
                return true;
            });

        var dataPlane = Substitute.For<IGeminiSubscriptionDataPlaneClient>();
        var body = new MemoryStream(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
        dataPlane.StreamAsync(Arg.Any<GeminiBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GeminiBrokerStreamResponse(
                response: null,
                stream: body,
                result: new ChatResult { Model = "gemini-2.5-pro", RouteKind = "broker-managed" },
                attempt: AccountAttemptResult.Success()));

        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);
        var service = new GeminiSubscriptionChatService(
            selector, repository, tenant, dataPlane,
            Options.Create(new GeminiSubscriptionOptions { ProviderId = providerId }));

        var result = await service.CompleteStreamingAsync(new ChatRequest
        {
            Model = "gemini-2.5-pro",
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
            PreferredProviderCode = "gemini-subscription",
        });

        result.IsSuccess.Should().BeTrue();
        attempts.Should().Be(2, "a lost race is retried exactly once with a fresh snapshot");
        reservedWith.Should().Equal(firstVersion, secondVersion);
        await result.DisposeAsync();
    }

    private static void SetVersion(ProviderAccount account, Guid version)
        => typeof(ProviderAccount).GetProperty(nameof(ProviderAccount.Version), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(account, version);
}
