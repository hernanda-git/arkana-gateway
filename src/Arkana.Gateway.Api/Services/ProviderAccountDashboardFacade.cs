using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.Broker;
using Arkana.Infrastructure.Persistence;
using Arkana.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Gateway.Api.Services;

public sealed class ProviderAccountDashboardFacade
{
    public static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly IProviderAccountRepository _accounts;
    private readonly ICLIProxyManagementClientFactory _clients;
    private readonly GatewayDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly IOAuthFlowService? _oauth;
    private readonly IOAuthPendingFlowRepository? _pending;

    public ProviderAccountDashboardFacade(
        IProviderAccountRepository accounts,
        ICLIProxyManagementClientFactory clients,
        GatewayDbContext db,
        ITenantProvider tenant,
        IOAuthFlowService? oauth = null,
        IOAuthPendingFlowRepository? pending = null)
    { _accounts = accounts; _clients = clients; _db = db; _tenant = tenant; _oauth = oauth; _pending = pending; }
    private Guid TenantId => _tenant.TenantId ?? throw new InvalidOperationException("A tenant is required.");

    public async Task<IReadOnlyList<ProviderAccountView>> ListAsync(CancellationToken ct = default)
        => (await _db.ProviderAccounts.AsNoTracking().Where(x => x.TenantId == TenantId && x.DeletedAt == null).OrderBy(x => x.Code).ToListAsync(ct)).Select(ToView).ToArray();
    public Task<ProviderAccount?> GetAsync(Guid id, CancellationToken ct) => _accounts.GetByIdAsync(TenantId, id, ct);
    public Task<GeminiSubscriptionOperationResult> StartAsync(Guid id, string slot, bool reconnect, string key, Guid expected, CancellationToken ct)
        => Run(reconnect ? ProviderAccountOperationKind.Reconnect : ProviderAccountOperationKind.Start, id, slot, null, reconnect ? "reconnect" : "start", expected, key, null,
            async (_, op) =>
            {
                var start = await _clients.Create(slot).StartOAuthAsync(ct);
                op.AuthorizationUrl = start.AuthorizationUrl;
                return new GeminiSubscriptionOperationResult(op.Id, ProviderAccountOperationState.Succeeded.ToString(), slot, start.AuthorizationUrl);
            }, ct);

    public async Task<BrokerOAuthCallbackHandlingResult?> TryHandleBrokerCallbackAsync(
        string providerCode,
        string? code,
        string? state,
        string? error,
        CancellationToken ct = default)
    {
        if (!string.Equals(providerCode, "gemini", StringComparison.OrdinalIgnoreCase)
            || !IsSafeBrokerOAuthState(state))
            return null;

        var hasCode = !string.IsNullOrWhiteSpace(code);
        var hasError = !string.IsNullOrWhiteSpace(error);
        if (hasCode == hasError)
            throw new ArgumentException("OAuth callback must contain exactly one result.");
        if (hasCode && !IsSafeBrokerOAuthValue(code, 8192))
            throw new ArgumentException("OAuth code is invalid.", nameof(code));
        if (hasError && !IsSafeBrokerOAuthValue(error, 1024))
            throw new ArgumentException("OAuth error is invalid.", nameof(error));

        var brokerState = state!;
        var now = DateTimeOffset.UtcNow;
        var candidates = await (
            from operation in _db.ProviderAccountOperations.AsNoTracking()
            join account in _db.ProviderAccounts.AsNoTracking()
                on operation.ProviderAccountId equals account.Id
            where (operation.Kind == ProviderAccountOperationKind.Start
                || operation.Kind == ProviderAccountOperationKind.Reconnect)
                && operation.State == ProviderAccountOperationState.Succeeded
                && operation.ExpiresAt > now
                && operation.AuthorizationUrl != null
                && operation.AuthorizationUrl.Contains(brokerState)
                && account.DeletedAt == null
                && account.AuthOwnership == ProviderAccountAuthOwnership.BrokerManagedOAuth
                && account.BrokerKind == BrokerKind.CLIProxyAPI
                && account.BrokerInstanceId == operation.Slot
            select new BrokerOAuthCallbackCandidate(operation.Slot, operation.AuthorizationUrl!))
            .ToListAsync(ct);

        var matches = candidates
            .Where(candidate => QueryParameterEquals(candidate.AuthorizationUrl, "state", brokerState))
            .ToArray();
        if (matches.Length == 0)
            return null;
        if (matches.Length != 1)
            throw new InvalidOperationException("OAuth state maps to multiple broker operations.");

        var submission = await _clients.Create(matches[0].Slot)
            .SubmitOAuthCallbackAsync(brokerState, code, error, ct);
        return new BrokerOAuthCallbackHandlingResult(true, submission.Disposition);
    }

    public async Task<OperationView?> OperationAsync(Guid id, Guid operationId, CancellationToken ct)
    {
        var op = await _db.ProviderAccountOperations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == operationId && x.ProviderAccountId == id && x.TenantId == TenantId, ct);
        return op is null ? null : new(op.Id, op.Kind.ToString(), op.State.ToString(), op.Slot, op.CreatedAt, op.UpdatedAt, op.ExpiresAt);
    }

    public async Task<IReadOnlyList<OperationView>> OperationsAsync(Guid id, CancellationToken ct = default)
        => (await _db.ProviderAccountOperations
            .AsNoTracking()
            .Where(x => x.ProviderAccountId == id && x.TenantId == TenantId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct))
            .Select(x => new OperationView(x.Id, x.Kind.ToString(), x.State.ToString(), x.Slot, x.CreatedAt, x.UpdatedAt, x.ExpiresAt))
            .ToArray();
    public Task<GeminiSubscriptionOperationResult> ProbeAsync(Guid id, string slot, string key, Guid expected, CancellationToken ct)
        => Run(ProviderAccountOperationKind.Probe, id, slot, null, "probe", expected, key, null, async (a, op) =>
        {
            var status = await BoundClient(a, slot).GetOAuthStatusAsync(ct);
            ApplyBrokerOAuthStatus(a, status);
            await _accounts.UpdateAsync(a, ct);
            return new GeminiSubscriptionOperationResult(op.Id, ProviderAccountOperationState.Succeeded.ToString(), slot);
        }, ct);
    public Task<GeminiSubscriptionOperationResult> CallbackAsync(Guid id, string slot, string redirectUrl, string key, Guid expected, CancellationToken ct)
    {
        ValidateCallback(redirectUrl);
        return Run(ProviderAccountOperationKind.Callback, id, slot, null, "callback", expected, key, redirectUrl, async (a, op) =>
        {
            var status = await BoundClient(a, slot).GetOAuthStatusAsync(ct);
            ApplyBrokerOAuthStatus(a, status);
            await _accounts.UpdateAsync(a, ct);
            return new GeminiSubscriptionOperationResult(op.Id, ProviderAccountOperationState.Succeeded.ToString(), slot);
        }, ct);
    }
    public Task<GeminiSubscriptionOperationResult> CancelAsync(Guid id, string slot, string auth, string key, Guid expected, CancellationToken ct)
        => Run(ProviderAccountOperationKind.Cancel, id, slot, auth, "cancel", expected, key, null, async (a, op) => { await BoundClient(a, slot).DisableAsync(auth, ct); a.Disable(); await _accounts.UpdateAsync(a, ct); return new GeminiSubscriptionOperationResult(op.Id, ProviderAccountOperationState.Succeeded.ToString(), slot); }, ct);
    public Task<ProviderAccountView> SetStateAsync(Guid id, string slot, string auth, bool enable, bool drain, string key, Guid expected, CancellationToken ct)
    { var kind = enable ? ProviderAccountOperationKind.Enable : drain ? ProviderAccountOperationKind.Drain : ProviderAccountOperationKind.Disable; var action = enable ? "enable" : drain ? "drain" : "disable"; return Run(kind, id, slot, auth, action, expected, key, null, async (a, _) => { var c = BoundClient(a, slot); if (enable) { await c.EnableAsync(auth, ct); a.Enable(); } else { await c.DisableAsync(auth, ct); if (drain) a.BeginDrain(); else a.Disable(); } await _accounts.UpdateAsync(a, ct); return ToView(a); }, ct); }
    public Task<bool> DeleteAsync(Guid id, string slot, string? auth, string actor, string key, Guid expected, CancellationToken ct)
        => Run(ProviderAccountOperationKind.Delete, id, slot, auth, "delete", expected, key, null, async (a, _) =>
        {
            // A pending account has no broker credential yet. It is still safe to
            // remove locally; connected accounts use the server-side credential
            // reference so the dashboard never needs to receive that identifier.
            var stableAuthId = auth ?? a.BrokerCredentialId;
            if (_pending is not null)
                await _pending.DeletePendingForAccountAsync(a.Code, a.TenantId, ct);

            if (a.AuthOwnership == ProviderAccountAuthOwnership.GatewayManagedOAuth)
            {
                if (auth is not null || !string.Equals(a.Code, slot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("OAuth account binding does not match the requested delete.");
                if (_oauth is null || _pending is null)
                    throw new InvalidOperationException("Gateway OAuth deletion is not configured.");

                await _oauth.DisconnectAsync(a.Code, ct);
            }
            else if (!string.IsNullOrWhiteSpace(stableAuthId))
            {
                await BoundClient(a, slot).DeleteAsync(stableAuthId, ct);
            }
            a.Tombstone(actor);
            await _accounts.UpdateAsync(a, ct);
            return true;
        }, ct);

    public async Task<int> CleanupDeletedAccountPendingFlowsAsync(Guid id, CancellationToken ct = default)
    {
        var account = await _db.ProviderAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == TenantId && x.Id == id, ct);
        if (account is null)
            throw new KeyNotFoundException("Provider account was not found.");
        if (account.DeletedAt is null)
            throw new InvalidOperationException("Pending-flow cleanup is only available for deleted accounts.");
        if (_pending is null)
            throw new InvalidOperationException("OAuth pending-flow cleanup is not configured.");

        await _pending.DeletePendingForAccountAsync(account.Code, account.TenantId, ct);
        return 1;
    }

    public async Task<bool> DeleteForProviderAsync(Guid providerId, string actor, string key, CancellationToken ct = default)
    {
        var accounts = await _db.ProviderAccounts
            .AsNoTracking()
            .Where(x => x.TenantId == TenantId && x.AiProviderId == providerId)
            .OrderBy(x => x.Code)
            .ToListAsync(ct);

        if (accounts.Count == 0)
            return false;

        if (accounts.Any(x => x.AuthOwnership is not (ProviderAccountAuthOwnership.BrokerManagedOAuth or ProviderAccountAuthOwnership.GatewayManagedOAuth)
            || (x.AuthOwnership == ProviderAccountAuthOwnership.BrokerManagedOAuth && x.BrokerKind != BrokerKind.CLIProxyAPI)))
        {
            throw new InvalidOperationException(
                "The provider has an unsupported account binding and must be removed through its account manager.");
        }

        var activeAccounts = accounts.Where(x => x.DeletedAt is null).ToArray();
        if (activeAccounts.Length == 0)
        {
            if (_pending is not null)
            {
                foreach (var account in accounts)
                    await _pending.DeletePendingForAccountAsync(account.Code, account.TenantId, ct);
            }
            return true;
        }

        foreach (var account in activeAccounts)
        {
            if (account.AuthOwnership == ProviderAccountAuthOwnership.BrokerManagedOAuth
                && string.IsNullOrWhiteSpace(account.BrokerInstanceId))
                throw new InvalidOperationException("Broker slot is missing from the provider account binding.");
        }

        foreach (var account in activeAccounts)
        {
            var slot = account.AuthOwnership == ProviderAccountAuthOwnership.GatewayManagedOAuth
                ? account.Code
                : account.BrokerInstanceId!;
            await DeleteAsync(
                account.Id,
                slot,
                null,
                actor,
                $"{key}-{account.Id:N}",
                account.Version,
                ct);
        }

        return true;
    }

    public async Task<IReadOnlySet<Guid>> GetDeletedProviderIdsAsync(CancellationToken ct = default)
    {
        var accounts = await _db.ProviderAccounts
            .AsNoTracking()
            .Where(x => x.TenantId == TenantId)
            .Select(x => new { x.AiProviderId, x.DeletedAt })
            .ToListAsync(ct);

        return accounts
            .GroupBy(x => x.AiProviderId)
            .Where(group => group.All(x => x.DeletedAt is not null))
            .Select(group => group.Key)
            .ToHashSet();
    }

    public async Task<DeleteRecoveryResult> RecoverDeleteAsync(
        Guid id,
        Guid operationId,
        string actor,
        string key,
        CancellationToken ct = default)
    {
        await using var mutationLease = await _accounts.AcquireMutationLeaseAsync(TenantId, id, ct);
        _ = NormalizeKey(key);

        var operation = await _db.ProviderAccountOperations
            .FirstOrDefaultAsync(x => x.TenantId == TenantId
                && x.ProviderAccountId == id
                && x.Id == operationId, ct);
        if (operation is null)
            throw new KeyNotFoundException("Provider account operation was not found.");
        if (operation.Kind != ProviderAccountOperationKind.Delete)
            throw new InvalidOperationException("Only delete operations can be recovered through this endpoint.");
        if (operation.State == ProviderAccountOperationState.Succeeded)
            return new(operation.Id, operation.State.ToString(), false, false);
        if (operation.State is not (ProviderAccountOperationState.Indeterminate
            or ProviderAccountOperationState.Pending
            or ProviderAccountOperationState.Running))
            throw new InvalidOperationException("The delete operation cannot be recovered from its current state.");
        if (operation.State is ProviderAccountOperationState.Pending or ProviderAccountOperationState.Running)
        {
            if (operation.ExpiresAt > DateTimeOffset.UtcNow)
                throw new InvalidOperationException("The delete operation is still in progress.");
            operation.State = ProviderAccountOperationState.Indeterminate;
            operation.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var account = await _db.ProviderAccounts
            .FirstOrDefaultAsync(x => x.TenantId == TenantId && x.Id == id, ct);
        if (account is null)
            throw new KeyNotFoundException("Provider account was not found.");

        var brokerCredentialFound = false;
        var brokerDeletePerformed = false;
        try
        {
            ValidateBinding(account, operation.Slot, null, ProviderAccountOperationKind.Delete);
            if (account.AuthOwnership == ProviderAccountAuthOwnership.GatewayManagedOAuth)
            {
                if (_oauth is null || _pending is null)
                    throw new InvalidOperationException("Gateway OAuth deletion is not configured.");
                await _oauth.DisconnectAsync(account.Code, ct);
                await _pending.DeletePendingForAccountAsync(account.Code, TenantId, ct);
            }
            else if (!string.IsNullOrWhiteSpace(account.BrokerCredentialId))
            {
                var client = _clients.Create(operation.Slot);
                var files = await client.ListAuthFilesAsync(ct);
                brokerCredentialFound = files.Files.Any(x =>
                    string.Equals(x.StableAuthId, account.BrokerCredentialId, StringComparison.Ordinal));
                if (brokerCredentialFound)
                {
                    await client.DeleteAsync(account.BrokerCredentialId, ct);
                    brokerDeletePerformed = true;
                }
            }

            account.Tombstone(actor);
            operation.State = ProviderAccountOperationState.Succeeded;
            operation.ResultJson = JsonSerializer.Serialize(true);
            operation.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return new(operation.Id, operation.State.ToString(), brokerCredentialFound, brokerDeletePerformed);
        }
        catch
        {
            // A broker/database boundary can fail after the external side effect.
            // Disable routing immediately and retain the indeterminate operation
            // so a later recovery read can reconcile without blind replay.
            account.MarkOutOfSync();
            operation.State = ProviderAccountOperationState.Indeterminate;
            operation.UpdatedAt = DateTimeOffset.UtcNow;
            try
            {
                await _db.SaveChangesAsync(CancellationToken.None);
            }
            catch
            {
                // The durable operation was already claimed before broker work;
                // a fresh recovery request can retry the read-side reconciliation.
            }
            throw;
        }
    }

    private async Task<T> Run<T>(ProviderAccountOperationKind kind, Guid id, string slot, string? auth, string action, Guid expected, string rawKey, string? redirect, Func<ProviderAccount, ProviderAccountOperation, Task<T>> work, CancellationToken ct)
    {
        await using var mutationLease = await _accounts.AcquireMutationLeaseAsync(TenantId, id, ct);
        var account = await GetAsync(id, ct) ?? throw new KeyNotFoundException("Provider account was not found.");
        ValidateBinding(account, slot, auth, kind);
        var key = NormalizeKey(rawKey);
        var fingerprint = Fingerprint(account, kind, slot, auth, action, expected, redirect);
        var stale = await _db.ProviderAccountOperations
            .Where(x => x.TenantId == TenantId && x.ProviderAccountId == id && x.Kind == kind &&
                (x.State == ProviderAccountOperationState.Pending || x.State == ProviderAccountOperationState.Running) &&
                x.ExpiresAt <= DateTimeOffset.UtcNow).ToListAsync(ct);
        foreach (var expired in stale) { expired.State = ProviderAccountOperationState.Expired; expired.UpdatedAt = DateTimeOffset.UtcNow; }
        if (stale.Count != 0) await _db.SaveChangesAsync(ct);
        var existing = await _db.ProviderAccountOperations.FirstOrDefaultAsync(x => x.TenantId == TenantId && x.ProviderAccountId == id && x.Kind == kind && x.IdempotencyKey == key, ct);
        if (existing is not null) { EnsureFingerprint(existing, fingerprint); return await ReadResultAsync<T>(existing, ct); }
        if (account.Version != expected) throw new ProviderAccountPreconditionException();
        var op = new ProviderAccountOperation { Id = Guid.NewGuid(), TenantId = TenantId, ProviderAccountId = id, Kind = kind, State = ProviderAccountOperationState.Running, Nonce = Guid.NewGuid().ToString("N"), IdempotencyKey = key, RequestFingerprint = fingerprint, ExpectedVersion = expected, Slot = slot.Trim(), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        try { _db.ProviderAccountOperations.Add(op); await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { var winner = await _db.ProviderAccountOperations.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == TenantId && x.ProviderAccountId == id && x.Kind == kind && x.IdempotencyKey == key, ct); if (winner is null) throw; EnsureFingerprint(winner, fingerprint); return await ReadResultAsync<T>(winner, ct); }

        var reservationVersion = Guid.NewGuid();
        if (!await _accounts.TryReserveVersionAsync(TenantId, id, expected, reservationVersion, ct))
        { op.State = ProviderAccountOperationState.Failed; op.UpdatedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(CancellationToken.None); throw new ProviderAccountPreconditionException(); }
        _db.Entry(account).State = EntityState.Detached;
        try
        {
            var current = await GetAsync(id, ct) ?? throw new KeyNotFoundException("Provider account was not found.");
            ValidateBinding(current, slot, auth, kind);
            if (current.Version != reservationVersion) throw new ProviderAccountPreconditionException();
            var result = await work(current, op);
            op.State = ProviderAccountOperationState.Succeeded; op.ResultJson = JsonSerializer.Serialize(result); op.UpdatedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); return result;
        }
        catch (ProviderAccountPreconditionException) { op.State = ProviderAccountOperationState.Failed; op.UpdatedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(CancellationToken.None); throw; }
        catch { op.State = ProviderAccountOperationState.Indeterminate; op.UpdatedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(CancellationToken.None); throw; }
    }

    private async Task<T> ReadResultAsync<T>(ProviderAccountOperation op, CancellationToken ct)
    {
        if (op.State == ProviderAccountOperationState.Succeeded)
            return op.ResultJson is null ? throw new InvalidOperationException("Operation result is unavailable; recovery is required.") : JsonSerializer.Deserialize<T>(op.ResultJson) ?? throw new InvalidOperationException("Operation result is invalid.");
        if ((op.State is ProviderAccountOperationState.Pending or ProviderAccountOperationState.Running) && op.ExpiresAt <= DateTimeOffset.UtcNow) { op.State = ProviderAccountOperationState.Indeterminate; op.UpdatedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(CancellationToken.None); }
        throw op.State switch { ProviderAccountOperationState.Pending or ProviderAccountOperationState.Running => new InvalidOperationException("Operation is still in progress."), ProviderAccountOperationState.Indeterminate => new InvalidOperationException("Operation outcome is unknown; recovery is required."), ProviderAccountOperationState.Failed => new InvalidOperationException("Operation failed."), _ => new InvalidOperationException("Operation cannot be replayed.") };
    }
    private static void EnsureFingerprint(ProviderAccountOperation op, string expected) { if (!string.Equals(op.RequestFingerprint, expected, StringComparison.Ordinal)) throw new ProviderAccountIdempotencyConflictException(); }
    private static void ApplyBrokerOAuthStatus(ProviderAccount account, CLIProxyOAuthStatus status)
    {
        if (status.State.Equals("connected", StringComparison.OrdinalIgnoreCase))
        {
            account.MarkConnected(status.ExpiresAt);
            if (!string.IsNullOrWhiteSpace(status.StableAuthId))
                account.BindBrokerCredential(status.StableAuthId);
        }
        else if (status.State.Equals("pending", StringComparison.OrdinalIgnoreCase)
            || status.State.Equals("authorizing", StringComparison.OrdinalIgnoreCase)
            || status.State.Equals("starting", StringComparison.OrdinalIgnoreCase))
        {
            account.MarkPending();
        }
        else
        {
            account.MarkReconnectRequired();
        }

        account.SyncFromBroker();
    }

    private ICLIProxyManagementClient BoundClient(ProviderAccount a, string slot) { ValidateBinding(a, slot, null); return _clients.Create(slot); }
    private static void ValidateBinding(
        ProviderAccount a,
        string slot,
        string? auth,
        ProviderAccountOperationKind? operationKind = null)
    {
        if (a.AuthOwnership == ProviderAccountAuthOwnership.GatewayManagedOAuth)
        {
            if (operationKind != ProviderAccountOperationKind.Delete
                || auth is not null
                || !string.Equals(a.Code, slot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OAuth account binding does not match the requested operation.");
            return;
        }

        if (a.AuthOwnership != ProviderAccountAuthOwnership.BrokerManagedOAuth
            || a.BrokerKind != BrokerKind.CLIProxyAPI
            || string.IsNullOrWhiteSpace(a.BrokerInstanceId)
            || !string.Equals(a.BrokerInstanceId, slot, StringComparison.Ordinal))
            throw new InvalidOperationException("Broker slot does not match the provider account binding.");
        if (auth is not null && !string.Equals(a.BrokerCredentialId, auth, StringComparison.Ordinal))
            throw new InvalidOperationException("Broker credential does not match the provider account binding.");
    }
    private sealed record BrokerOAuthCallbackCandidate(string Slot, string AuthorizationUrl);

    private static bool IsSafeBrokerOAuthState(string? state)
        => !string.IsNullOrWhiteSpace(state)
            && state.Length <= 256
            && state.All(ch => ch is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-' or '_' or '.' or '~');

    private static bool IsSafeBrokerOAuthValue(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(ch => ch is >= '\x21' and <= '\x7e');

    private static bool QueryParameterEquals(string rawUrl, string parameter, string expected)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            return false;
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf('=');
            var rawKey = separator < 0 ? item : item[..separator];
            var rawValue = separator < 0 ? string.Empty : item[(separator + 1)..];
            try
            {
                if (string.Equals(Uri.UnescapeDataString(rawKey), parameter, StringComparison.Ordinal)
                    && string.Equals(Uri.UnescapeDataString(rawValue), expected, StringComparison.Ordinal))
                    return true;
            }
            catch (UriFormatException)
            {
                return false;
            }
        }
        return false;
    }

    private static string NormalizeKey(string key) { if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsWhiteSpace)) throw new ArgumentException("Idempotency-Key header is required."); return key.Trim(); }
    private static string Fingerprint(ProviderAccount a, ProviderAccountOperationKind kind, string slot, string? auth, string action, Guid version, string? redirect) { var raw = string.Join("\n", a.TenantId, a.Id, kind, slot.Trim(), a.BrokerCredentialId ?? "", auth ?? "", action, version, redirect?.Trim() ?? ""); return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(); }
    public static void ValidateCallback(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || (!uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Callback URL is invalid.", nameof(value)); }
    private static ProviderAccountView ToView(ProviderAccount a) => new(a.Id, a.Code, a.DisplayName, a.ConnectionStatus.ToString(), a.IsEnabled, a.Version, a.CreatedAt, a.UpdatedAt, a.DeletedAt, a.LastBrokerSyncAt);
}

public sealed record BrokerOAuthCallbackHandlingResult(bool Handled, CLIProxyOAuthCallbackDisposition Disposition);
public sealed record GeminiSubscriptionOperationResult(Guid OperationId, string State, string Slot, string? AuthorizationUrl = null);
public sealed record DeleteRecoveryResult(Guid OperationId, string State, bool BrokerCredentialFound, bool BrokerDeletePerformed);
public sealed class ProviderAccountPreconditionException : Exception;
public sealed class ProviderAccountIdempotencyConflictException : Exception;
public sealed record ProviderAccountView(Guid Id, string Code, string DisplayName, string Status, bool IsEnabled, Guid Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt, DateTimeOffset? LastBrokerSyncAt);
public sealed record OperationView(Guid Id, string Kind, string State, string Slot, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset ExpiresAt);
public sealed record ProviderAccountSlotRequest(string Slot, string? StableAuthId = null);
public sealed record ProviderAccountCallbackRequest(string Slot, string RedirectUrl);
