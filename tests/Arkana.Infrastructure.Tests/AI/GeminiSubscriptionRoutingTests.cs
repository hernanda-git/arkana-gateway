using Arkana.Application.Features.Chat;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.AI;
using Arkana.Infrastructure.Broker;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.AI;

public sealed class GeminiSubscriptionRoutingTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Provider = Guid.NewGuid();

    private static ProviderAccount Account(string code, bool connected = true)
    {
        var account = ProviderAccount.Create(Tenant, Provider, code, code, ProviderAccountAuthOwnership.BrokerManagedOAuth, BrokerKind.CLIProxyAPI, brokerInstanceId: code);
        if (connected) account.MarkConnected();
        return account;
    }

    [Theory]
    [InlineData(AccountRoutingMode.Pool, "a", "a")]
    [InlineData(AccountRoutingMode.StrictPin, "b", "b")]
    [InlineData(AccountRoutingMode.PinWithSameProviderFailover, "b", "b")]
    public async Task Selector_is_deterministic_for_pool_and_pin_modes(AccountRoutingMode mode, string? pin, string expected)
    {
        var repo = Substitute.For<IProviderAccountRepository>();
        repo.GetForProviderAsync(Tenant, Provider, false, Arg.Any<CancellationToken>()).Returns([Account("b"), Account("a")]);
        var selector = new ProviderAccountSelector(repo);
        var result = await selector.SelectAsync(new(Tenant, Provider, "gemini-2.5-pro", mode, pin));
        result.Account!.Code.Should().Be(expected);
    }

    [Fact]
    public async Task Selector_excludes_disabled_cooling_and_unsupported_accounts()
    {
        var cooling = Account("cooling"); cooling.SetCooldown(DateTimeOffset.UtcNow.AddMinutes(1));
        var unsupported = Account("unsupported"); unsupported.SetSupportedModels(["other"]);
        var disabled = Account("disabled"); disabled.Disable();
        var good = Account("good");
        var repo = Substitute.For<IProviderAccountRepository>();
        repo.GetForProviderAsync(Tenant, Provider, false, Arg.Any<CancellationToken>()).Returns([cooling, unsupported, disabled, good]);
        var result = await new ProviderAccountSelector(repo).SelectAsync(new(Tenant, Provider, "gemini-2.5-pro"));
        result.Account!.Code.Should().Be("good");
    }

    [Fact]
    public async Task Strict_pin_does_not_fall_back_when_pin_is_unavailable()
    {
        var repo = Substitute.For<IProviderAccountRepository>();
        repo.GetForProviderAsync(Tenant, Provider, false, Arg.Any<CancellationToken>()).Returns([Account("good")]);
        var result = await new ProviderAccountSelector(repo).SelectAsync(new(Tenant, Provider, "model", AccountRoutingMode.StrictPin, "missing"));
        result.HasAccount.Should().BeFalse();
    }

    [Fact]
    public async Task Explicit_account_pin_uses_the_pinned_provider_id_for_selection()
    {
        var accountProvider = Guid.NewGuid();
        var configuredPoolProvider = Guid.NewGuid();
        var account = ProviderAccount.Create(
            Tenant,
            accountProvider,
            "gemini-acc7",
            "Antigravity One",
            ProviderAccountAuthOwnership.BrokerManagedOAuth,
            BrokerKind.CLIProxyAPI,
            brokerInstanceId: "gemini-broker-a");
        account.MarkConnected();
        account.SetSupportedModels(["gemini-3-flash"]);

        var selector = Substitute.For<IProviderAccountSelector>();
        selector.SelectAsync(Arg.Any<ProviderAccountSelectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProviderAccountSelectionResult(account, [account], null));
        var repository = Substitute.For<IProviderAccountRepository>();
        repository.AcquireMutationLeaseAsync(Tenant, account.Id, Arg.Any<CancellationToken>())
            .Returns(new NoopAsyncDisposable());
        repository.GetByIdNoTrackingAsync(Tenant, account.Id, Arg.Any<CancellationToken>()).Returns(account);
        repository.TryReserveVersionAsync(Tenant, account.Id, account.Version, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                typeof(ProviderAccount).GetProperty(nameof(ProviderAccount.Version))!
                    .SetValue(account, callInfo.ArgAt<Guid>(3));
                return true;
            });
        repository.TryUpdateDataPlaneHealthAsync(account, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var dataPlane = Substitute.For<IGeminiSubscriptionDataPlaneClient>();
        dataPlane.CompleteAsync(Arg.Any<GeminiBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GeminiBrokerResponse(
                new ChatResult { Content = "ok", Model = "gemini-3-flash" },
                AccountAttemptResult.Success()));
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(Tenant);
        var service = new GeminiSubscriptionChatService(
            selector,
            repository,
            tenant,
            dataPlane,
            Options.Create(new GeminiSubscriptionOptions { ProviderId = configuredPoolProvider }));

        var result = await service.CompleteAsync(new ChatRequest
        {
            TenantId = Tenant,
            Model = "gemini-3-flash",
            PreferredProviderCode = "gemini-acc7",
            PreferredProviderId = accountProvider,
            Messages = [new ChatMessage { Role = "user", Content = "hello" }]
        });

        result.IsSuccess.Should().BeTrue();
        await selector.Received(1).SelectAsync(
            Arg.Is<ProviderAccountSelectionRequest>(request =>
                request.ProviderId == accountProvider
                && request.PinnedAccountCode == "gemini-acc7"
                && request.BrokerManagedOnly),
            Arg.Any<CancellationToken>());
        await dataPlane.Received(1).CompleteAsync(
            Arg.Is<GeminiBrokerRequest>(request =>
                request.Slot == "gemini-broker-a" && request.AccountCode == "gemini-acc7"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(401, UpstreamFailureClass.Unauthorized, false)]
    [InlineData(403, UpstreamFailureClass.Forbidden, false)]
    [InlineData(429, UpstreamFailureClass.RateLimited, true)]
    [InlineData(500, UpstreamFailureClass.ServerError, true)]
    public void Classifier_maps_http_failures(int status, UpstreamFailureClass expected, bool retryable)
    {
        var result = UpstreamFailureClassifier.Classify((System.Net.HttpStatusCode)status);
        result.FailureClass.Should().Be(expected);
        result.Retryable.Should().Be(retryable);
    }

    [Fact]
    public void Classifier_bounds_quota_cooldown_and_disables_retry_after_commit()
    {
        var result = UpstreamFailureClassifier.Classify(System.Net.HttpStatusCode.TooManyRequests, "99999", streamingCommitted: true);
        result.Retryable.Should().BeFalse();
        UpstreamFailureClassifier.BoundedQuotaCooldown(result)!.Value.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task CompleteAsync_fences_reservation_with_fresh_snapshot_when_selector_snapshot_is_stale()
    {
        // Regression: this service can be resolved from a long-lived captured graph, so the
        // selector may hand out a snapshot whose (tracked) Version predates an out-of-band
        // change. The reservation must be fenced against a fresh no-tracking read, never
        // against a stale snapshot — otherwise every request fails with
        // "Provider account changed; retry the request.".
        var stale = Account("gemini-acc3");
        var fresh = Account("gemini-acc3");
        var freshVersion = fresh.Version;

        var selector = Substitute.For<IProviderAccountSelector>();
        selector.SelectAsync(Arg.Any<ProviderAccountSelectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProviderAccountSelectionResult(stale, [stale], null));

        var reservedWith = new List<Guid>();
        var repository = Substitute.For<IProviderAccountRepository>();
        repository.AcquireMutationLeaseAsync(Tenant, stale.Id, Arg.Any<CancellationToken>())
            .Returns(new NoopAsyncDisposable());
        repository.GetByIdNoTrackingAsync(Tenant, stale.Id, Arg.Any<CancellationToken>()).Returns(fresh);
        repository.TryReserveVersionAsync(Tenant, stale.Id, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                reservedWith.Add(callInfo.ArgAt<Guid>(2));
                typeof(ProviderAccount).GetProperty(nameof(ProviderAccount.Version))!
                    .SetValue(fresh, callInfo.ArgAt<Guid>(3));
                return true;
            });
        repository.TryUpdateDataPlaneHealthAsync(fresh, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var dataPlane = Substitute.For<IGeminiSubscriptionDataPlaneClient>();
        dataPlane.CompleteAsync(Arg.Any<GeminiBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GeminiBrokerResponse(
                new ChatResult { Content = "ok", Model = "gemini-3-flash" },
                AccountAttemptResult.Success()));
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(Tenant);

        var service = new GeminiSubscriptionChatService(
            selector, repository, tenant, dataPlane,
            Options.Create(new GeminiSubscriptionOptions { ProviderId = Provider }));

        var result = await service.CompleteAsync(new ChatRequest
        {
            TenantId = Tenant,
            Model = "gemini-3-flash",
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
        });

        result.IsSuccess.Should().BeTrue();
        reservedWith.Should().ContainSingle()
            .Which.Should().Be(freshVersion, "the reservation must be fenced by the fresh snapshot");
        stale.Version.Should().NotBe(freshVersion, "the selector snapshot is deliberately stale");
    }

    [Fact]
    public async Task CompleteAsync_retries_reservation_once_when_the_first_attempt_loses_a_race()
    {
        var tenantId = Tenant;
        var pinned = Account("gemini-acc4");
        var first = Account("gemini-acc4");
        var second = Account("gemini-acc4");
        var firstVersion = first.Version;
        var secondVersion = second.Version;

        var selector = Substitute.For<IProviderAccountSelector>();
        selector.SelectAsync(Arg.Any<ProviderAccountSelectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProviderAccountSelectionResult(pinned, [pinned], null));

        var reservedWith = new List<Guid>();
        var attempts = 0;
        var repository = Substitute.For<IProviderAccountRepository>();
        repository.AcquireMutationLeaseAsync(tenantId, pinned.Id, Arg.Any<CancellationToken>())
            .Returns(new NoopAsyncDisposable());
        repository.GetByIdNoTrackingAsync(tenantId, pinned.Id, Arg.Any<CancellationToken>())
            .Returns(first, second, second);
        repository.TryReserveVersionAsync(tenantId, pinned.Id, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                attempts++;
                reservedWith.Add(callInfo.ArgAt<Guid>(2));
                if (attempts == 1)
                    return false;
                typeof(ProviderAccount).GetProperty(nameof(ProviderAccount.Version))!
                    .SetValue(second, callInfo.ArgAt<Guid>(3));
                return true;
            });
        repository.TryUpdateDataPlaneHealthAsync(second, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var dataPlane = Substitute.For<IGeminiSubscriptionDataPlaneClient>();
        dataPlane.CompleteAsync(Arg.Any<GeminiBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GeminiBrokerResponse(
                new ChatResult { Content = "ok", Model = "gemini-3-flash" },
                AccountAttemptResult.Success()));
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(tenantId);

        var service = new GeminiSubscriptionChatService(
            selector, repository, tenant, dataPlane,
            Options.Create(new GeminiSubscriptionOptions { ProviderId = Provider }));

        var result = await service.CompleteAsync(new ChatRequest
        {
            TenantId = tenantId,
            Model = "gemini-3-flash",
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
        });

        result.IsSuccess.Should().BeTrue();
        attempts.Should().Be(2, "a lost race is retried exactly once with a fresh snapshot");
        reservedWith.Should().Equal(firstVersion, secondVersion);
    }

    [Fact]
    public async Task CompleteAsync_fails_closed_when_both_reservation_attempts_lose()
    {
        var pinned = Account("gemini-acc5");
        var selector = Substitute.For<IProviderAccountSelector>();
        selector.SelectAsync(Arg.Any<ProviderAccountSelectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProviderAccountSelectionResult(pinned, [pinned], null));

        var repository = Substitute.For<IProviderAccountRepository>();
        repository.AcquireMutationLeaseAsync(Tenant, pinned.Id, Arg.Any<CancellationToken>())
            .Returns(new NoopAsyncDisposable());
        repository.GetByIdNoTrackingAsync(Tenant, pinned.Id, Arg.Any<CancellationToken>()).Returns(pinned);
        repository.TryReserveVersionAsync(Tenant, pinned.Id, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(Tenant);

        var service = new GeminiSubscriptionChatService(
            selector, repository, tenant,
            Substitute.For<IGeminiSubscriptionDataPlaneClient>(),
            Options.Create(new GeminiSubscriptionOptions { ProviderId = Provider }));

        var result = await service.CompleteAsync(new ChatRequest
        {
            TenantId = Tenant,
            Model = "gemini-3-flash",
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
        });

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("Provider account changed; retry the request.");
        await repository.Received(2).TryReserveVersionAsync(
            Tenant, pinned.Id, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
