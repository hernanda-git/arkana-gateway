using Arkana.Application.Features.Chat;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.Broker;

namespace Arkana.Infrastructure.AI;

public sealed class GeminiSubscriptionOptions
{
    public const string Section = "GeminiSubscription";
    public Guid ProviderId { get; set; }
    public AccountRoutingMode RoutingMode { get; set; } = AccountRoutingMode.Pool;
    public TimeSpan DefaultQuotaCooldown { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Routes only explicitly broker-owned Gemini accounts; native gemini remains separate.</summary>
internal sealed class GeminiSubscriptionChatService : IChatCompletionService, IStreamingChatCompletionService
{
    private readonly IProviderAccountSelector _selector;
    private readonly IProviderAccountRepository _repository;
    private readonly ITenantProvider _tenant;
    private readonly IGeminiSubscriptionDataPlaneClient _dataPlane;
    private readonly GeminiSubscriptionOptions _options;

    public GeminiSubscriptionChatService(IProviderAccountSelector selector, IProviderAccountRepository repository, ITenantProvider tenant,
        IGeminiSubscriptionDataPlaneClient dataPlane, Microsoft.Extensions.Options.IOptions<GeminiSubscriptionOptions> options)
    { _selector = selector; _repository = repository; _tenant = tenant; _dataPlane = dataPlane; _options = options.Value; }

    public string ProviderName => "gemini-subscription";

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        if (!_tenant.TenantId.HasValue)
            return new() { Model = request.Model, ErrorMessage = "Gemini subscription routing is not configured." };
        // The stable broker alias identifies ownership, not an account. Only an
        // explicit gemini-accN identity is an account pin; aliases must pool-select.
        var pin = request.PreferredProviderCode;
        var isAccountPin = pin?.StartsWith("gemini-acc", StringComparison.OrdinalIgnoreCase) == true;
        var effectivePin = isAccountPin ? pin : null;
        var selectionProviderId = isAccountPin && request.PreferredProviderId is { } pinnedProviderId
            && pinnedProviderId != Guid.Empty
            ? pinnedProviderId
            : _options.ProviderId;
        if (selectionProviderId == Guid.Empty)
            return new() { Model = request.Model, ErrorMessage = "Gemini subscription routing is not configured." };
        var mode = request.AccountRoutingMode == AccountRoutingMode.Pool && effectivePin is not null
            ? (_options.RoutingMode == AccountRoutingMode.Pool ? AccountRoutingMode.StrictPin : _options.RoutingMode)
            : request.AccountRoutingMode;
        var selection = await _selector.SelectAsync(new ProviderAccountSelectionRequest(
            _tenant.TenantId.Value, selectionProviderId, request.Model, mode, effectivePin, BrokerManagedOnly: true), ct);
        var account = selection.Account;
        if (account is null || !IsEligibleBrokerAccount(account))
            return new() { Model = request.Model, ErrorMessage = selection.RejectionReason ?? "No eligible Gemini subscription account." };
        await using var mutationLease = await _repository.AcquireMutationLeaseAsync(_tenant.TenantId.Value, account.Id, ct);
        var reservation = await ReserveEligibleAccountAsync(_tenant.TenantId.Value, account.Id, ct);
        if (reservation.Current is null)
            return new() { Model = request.Model, ErrorMessage = reservation.Failure! };
        var current = reservation.Current;
        var reservationVersion = reservation.Reservation;
        var trace = Guid.NewGuid().ToString("N");
        var response = await _dataPlane.CompleteAsync(new GeminiBrokerRequest(request, current.BrokerInstanceId!, current.Code, trace, "gemini"), ct);
        var cooldown = UpstreamFailureClassifier.BoundedQuotaCooldown(response.Attempt, _options.DefaultQuotaCooldown);
        if (cooldown.HasValue && cooldown.Value > TimeSpan.Zero)
        {
            current.SetCooldown(DateTimeOffset.UtcNow.Add(cooldown.Value), response.Attempt.FailureClass.ToString());
            await _repository.TryUpdateDataPlaneHealthAsync(current, reservationVersion, ct);
        }
        else if (response.Attempt.Succeeded)
        {
            current.RecordSuccess();
            await _repository.TryUpdateDataPlaneHealthAsync(current, reservationVersion, ct);
        }
        return response.Result with
        {
            ResolvedProviderAccountId = current.Id,
            ResolvedProviderAccountCode = current.Code,
            RouteKind = "broker-managed",
        };
    }

    public async Task<ChatStreamResult> CompleteStreamingAsync(ChatRequest request, CancellationToken ct = default)
    {
        if (!_tenant.TenantId.HasValue || _options.ProviderId == Guid.Empty)
            return new(null, new ChatResult
            {
                Model = request.Model,
                ErrorMessage = "Gemini subscription routing is not configured.",
                RouteKind = "broker-managed",
            });

        var tenantId = _tenant.TenantId.Value;
        var pin = request.PreferredProviderCode;
        var isAccountPin = pin?.StartsWith("gemini-acc", StringComparison.OrdinalIgnoreCase) == true;
        var effectivePin = isAccountPin ? pin : null;
        var selectionProviderId = isAccountPin && request.PreferredProviderId is { } pinnedProviderId
            && pinnedProviderId != Guid.Empty
            ? pinnedProviderId
            : _options.ProviderId;
        if (selectionProviderId == Guid.Empty)
            return new(null, new ChatResult
            {
                Model = request.Model,
                ErrorMessage = "Gemini subscription routing is not configured.",
                RouteKind = "broker-managed",
            });
        var mode = request.AccountRoutingMode == AccountRoutingMode.Pool && effectivePin is not null
            ? (_options.RoutingMode == AccountRoutingMode.Pool ? AccountRoutingMode.StrictPin : _options.RoutingMode)
            : request.AccountRoutingMode;
        var selection = await _selector.SelectAsync(new ProviderAccountSelectionRequest(
            tenantId, selectionProviderId, request.Model, mode, effectivePin, BrokerManagedOnly: true), ct);
        var selected = selection.Account;
        if (selected is null || selected.AuthOwnership != ProviderAccountAuthOwnership.BrokerManagedOAuth
            || selected.BrokerKind != BrokerKind.CLIProxyAPI || string.IsNullOrWhiteSpace(selected.BrokerInstanceId))
        {
            return new(null, new ChatResult
            {
                Model = request.Model,
                ErrorMessage = selection.RejectionReason ?? "No eligible Gemini subscription account.",
                RouteKind = "broker-managed",
            });
        }

        var mutationLease = await _repository.AcquireMutationLeaseAsync(tenantId, selected.Id, ct);
        var handedOff = false;
        try
        {
            var reservation = await ReserveEligibleAccountAsync(tenantId, selected.Id, ct);
            if (reservation.Current is null)
                return new(null, new ChatResult { Model = request.Model, ErrorMessage = reservation.Failure!, RouteKind = "broker-managed" });
            var current = reservation.Current;
            var reservationVersion = reservation.Reservation;

            var trace = Guid.NewGuid().ToString("N");
            var response = await _dataPlane.StreamAsync(
                new GeminiBrokerRequest(request, current.BrokerInstanceId!, current.Code, trace, "gemini"), ct);
            var result = response.Result with
            {
                ResolvedProviderAccountId = current.Id,
                ResolvedProviderAccountCode = current.Code,
                RouteKind = "broker-managed",
            };

            if (!response.IsSuccess)
            {
                await FinalizeAccountAsync(current, reservationVersion, response.Attempt);
                await response.DisposeAsync();
                return new(null, result);
            }

            handedOff = true;
            return new(response.Stream, result, async completed =>
            {
                try
                {
                    var attempt = completed
                        ? AccountAttemptResult.Success()
                        : AccountAttemptResult.Failure(UpstreamFailureClass.Network, committed: true);
                    await FinalizeAccountAsync(current, reservationVersion, attempt);
                }
                finally
                {
                    await response.DisposeAsync();
                    await mutationLease.DisposeAsync();
                }
            });
        }
        finally
        {
            if (!handedOff)
                await mutationLease.DisposeAsync();
        }
    }

    private static bool IsEligibleBrokerAccount(ProviderAccount? account)
        => account is not null
            && account.AuthOwnership == ProviderAccountAuthOwnership.BrokerManagedOAuth
            && account.BrokerKind == BrokerKind.CLIProxyAPI
            && !string.IsNullOrWhiteSpace(account.BrokerInstanceId);

    /// <summary>
    /// Reserves the account version while the caller holds the mutation lease.
    /// The snapshot is read tracker-independently (no-tracking) because this
    /// service can be resolved from a long-lived captured graph: a tracked
    /// snapshot from an earlier request would otherwise fence the reservation
    /// with a stale version and reject every request after any out-of-band
    /// version change ("Provider account changed"). A lost race against a
    /// non-lease writer is retried once with a fresh snapshot before failing
    /// closed.
    /// </summary>
    private async Task<(ProviderAccount? Current, Guid Reservation, string? Failure)> ReserveEligibleAccountAsync(
        Guid tenantId, Guid accountId, CancellationToken ct)
    {
        const string changed = "Provider account changed; retry the request.";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var snapshot = await _repository.GetByIdNoTrackingAsync(tenantId, accountId, ct);
            if (snapshot is null || !IsEligibleBrokerAccount(snapshot))
                return (null, Guid.Empty, "No eligible Gemini subscription account.");
            var reservation = Guid.NewGuid();
            if (!await _repository.TryReserveVersionAsync(tenantId, accountId, snapshot.Version, reservation, ct))
                continue;
            var current = await _repository.GetByIdNoTrackingAsync(tenantId, accountId, ct);
            return current is not null
                && IsEligibleBrokerAccount(current)
                && current.BrokerInstanceId == snapshot.BrokerInstanceId
                && current.Version == reservation
                ? (current, reservation, null)
                : (null, Guid.Empty, changed);
        }
        return (null, Guid.Empty, changed);
    }

    private async Task FinalizeAccountAsync(ProviderAccount account, Guid reservationVersion, AccountAttemptResult attempt)
    {
        var cooldown = UpstreamFailureClassifier.BoundedQuotaCooldown(attempt, _options.DefaultQuotaCooldown);
        if (cooldown.HasValue && cooldown.Value > TimeSpan.Zero)
            account.SetCooldown(DateTimeOffset.UtcNow.Add(cooldown.Value), attempt.FailureClass.ToString());
        else if (attempt.Succeeded)
            account.RecordSuccess();
        else
            account.RecordFailure(attempt.FailureClass == UpstreamFailureClass.None ? "stream-failed" : attempt.FailureClass.ToString());

        await _repository.TryUpdateDataPlaneHealthAsync(account, reservationVersion, CancellationToken.None);
    }
}
