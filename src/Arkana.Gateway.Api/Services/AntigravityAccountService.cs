using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Infrastructure.Broker;
using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arkana.Gateway.Api.Services;

/// <summary>
/// Creates and lists Antigravity subscription accounts backed by one isolated
/// CLIProxyAPI slot per Google identity. Native Gemini API OAuth is deliberately
/// not involved in this flow.
/// </summary>
public sealed class AntigravityAccountService
{
    private const long AccountAllocationLockKey = 5819437261841L;

    private static readonly (string Name, string Code)[] AccountModels =
    [
        ("Gemini 3 Flash", "gemini-3-flash"),
        ("Gemini 3.6 Flash High", "gemini-3.6-flash-high"),
        ("Gemini 3.7 Flash High", "gemini-3.7-flash-high"),
        ("Gemini 3.1 Pro Low", "gemini-3.1-pro-low"),
        ("Gemini Pro Agent", "gemini-pro-agent"),
        ("Claude Sonnet 4.6", "claude-sonnet-4-6"),
    ];

    private readonly IDbContextFactory<GatewayDbContext> _dbFactory;
    private readonly ITenantProvider _tenant;
    private readonly ICLIProxyManagementClientFactory _clients;
    private readonly CLIProxyManagementOptions _options;
    private readonly ILogger<AntigravityAccountService> _logger;

    public AntigravityAccountService(
        IDbContextFactory<GatewayDbContext> dbFactory,
        ITenantProvider tenant,
        ICLIProxyManagementClientFactory clients,
        IOptions<CLIProxyManagementOptions> options,
        ILogger<AntigravityAccountService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _clients = clients;
        _options = options.Value;
        _logger = logger;
    }

    private Guid TenantId => _tenant.TenantId
        ?? throw new InvalidOperationException("Authenticated tenant is required.");

    public string DefaultSlot => _options.DefaultSlot;

    public IReadOnlyList<string> ConfiguredSlots
        => _options.Slots.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<IReadOnlyList<AntigravityAccountView>> ListAsync(CancellationToken ct = default)
    {
        var tenantId = TenantId;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var accounts = await db.ProviderAccounts
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && x.DeletedAt == null
                && x.AuthOwnership == ProviderAccountAuthOwnership.BrokerManagedOAuth
                && x.BrokerKind == BrokerKind.CLIProxyAPI)
            .OrderBy(x => x.Code)
            .ToListAsync(ct);

        return accounts.Select(ToView).ToArray();
    }

    /// <summary>
    /// Creates a broker-managed provider, its model catalog, and its account
    /// projection atomically. The Postgres advisory lock serializes account-code
    /// allocation across gateway instances; the in-memory test provider skips it.
    /// </summary>
    public async Task<AntigravityAccountView> CreateAsync(
        string label,
        string? slot = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("Account label is required.", nameof(label));

        var slotName = CanonicalizeSlot(slot);

        // The factory validates the canonical slot configuration before any DB mutation.
        _ = _clients.Create(slotName);

        var tenantId = TenantId;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        IDbContextTransaction? transaction = null;
        try
        {
            if (db.Database.IsRelational())
            {
                transaction = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock({0});",
                    new object[] { AccountAllocationLockKey },
                    ct);
            }

            var activeSlots = await db.ProviderAccounts
                .AsNoTracking()
                .Where(x => x.BrokerInstanceId != null && x.DeletedAt == null)
                .Select(x => x.BrokerInstanceId!)
                .ToListAsync(ct);
            var slotInUse = activeSlots.Any(existing =>
                string.Equals(existing, slotName, StringComparison.OrdinalIgnoreCase));
            if (slotInUse)
                throw new InvalidOperationException(
                    $"Broker slot '{slotName}' is already assigned to an active Antigravity account. Reconnect or delete that account before creating another.");

            var existingCodes = await db.AiProviders
                // Provider codes were globally unique in the legacy schema. Keep
                // allocation global so an old row with a missing tenant cannot
                // collide with the new broker projection after deployment.
                .Where(x => x.Code.StartsWith("gemini-acc"))
                .Select(x => x.Code)
                .ToListAsync(ct);
            var accountNumber = NextAccountNumber(existingCodes);
            var code = $"gemini-acc{accountNumber}";
            var displayName = label.Trim();

            var provider = AiProvider.Create(displayName, code, 100 + accountNumber);
            provider.AssignTenant(tenantId);

            var modelCodes = AccountModels.Select(x => x.Code).ToArray();
            foreach (var (name, modelCode) in AccountModels)
                db.Models.Add(Model.Create(provider.Id, name, modelCode, 0, 0));

            var account = ProviderAccount.Create(
                tenantId,
                provider.Id,
                code,
                displayName,
                authOwnership: ProviderAccountAuthOwnership.BrokerManagedOAuth,
                brokerKind: BrokerKind.CLIProxyAPI,
                brokerInstanceId: slotName);
            account.SetSupportedModels(modelCodes);

            db.AiProviders.Add(provider);
            db.ProviderAccounts.Add(account);
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            _logger.LogInformation(
                "Created Antigravity broker account {Code} on slot {Slot}.",
                code, slotName);
            return ToView(account);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private string CanonicalizeSlot(string? slot)
    {
        var requested = string.IsNullOrWhiteSpace(slot) ? DefaultSlot : slot.Trim();
        if (string.IsNullOrWhiteSpace(requested))
            throw new InvalidOperationException("An Antigravity broker slot is not configured.");

        var configured = _options.Slots.Keys.FirstOrDefault(key =>
            string.Equals(key, requested, StringComparison.OrdinalIgnoreCase));
        return configured
            ?? throw new ArgumentException($"Broker slot '{requested}' is not allowlisted.", nameof(slot));
    }

    private static int NextAccountNumber(IEnumerable<string> codes)
    {
        var max = 0;
        foreach (var code in codes)
        {
            var suffix = code["gemini-acc".Length..];
            if (int.TryParse(suffix, out var number) && number > max)
                max = number;
        }

        return max + 1;
    }

    private static AntigravityAccountView ToView(ProviderAccount account)
        => new(
            account.AiProviderId,
            account.Id,
            account.Code,
            account.DisplayName,
            account.BrokerInstanceId ?? string.Empty,
            account.ConnectionStatus.ToString(),
            account.IsEnabled,
            account.Version,
            (account.SupportedModels ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

public sealed record AntigravityAccountView(
    Guid ProviderId,
    Guid AccountId,
    string Code,
    string DisplayName,
    string Slot,
    string Status,
    bool IsEnabled,
    Guid Version,
    IReadOnlyList<string> SupportedModels);
