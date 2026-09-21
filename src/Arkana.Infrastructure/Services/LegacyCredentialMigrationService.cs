using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Arkana.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// One-shot migration that encrypts legacy plaintext <c>ApiProvider.ApiKey</c>
/// rows in the database. Runs on startup, before <see cref="ApiKeyBootstrapService"/>.
///
/// Background:
/// Before envelope encryption was introduced, provider API keys were stored as
/// plaintext in the <c>AiProviders</c> table. Rows from that era start with
/// neither <c>v1:</c> (production vault format) nor <c>testv1:</c> (test double).
/// This service detects those legacy rows, encrypts them with the active vault,
/// and writes them back. After the first run, the column is uniformly sealed.
///
/// Idempotent: a row whose <c>ApiKey</c> already starts with <c>v1:</c> is left
/// alone. A row with no key at all is left alone (the bootstrap service handles
/// new keys from env vars).
/// </summary>
internal sealed class LegacyCredentialMigrationService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<LegacyCredentialMigrationService> _logger;

    public LegacyCredentialMigrationService(
        IServiceProvider services,
        ILogger<LegacyCredentialMigrationService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var vault = scope.ServiceProvider.GetRequiredService<ICredentialVault>();

            // Find rows whose ApiKey is set, non-empty, and NOT in the sealed
            // v1: format. These are legacy plaintext rows.
            var legacyRows = await db.AiProviders
                .Where(p => p.ApiKey != null && p.ApiKey != "" &&
                            !(p.ApiKey!.StartsWith("v1:")))
                .ToListAsync(ct);

            if (legacyRows.Count == 0)
            {
                _logger.LogDebug("No legacy plaintext credentials to migrate.");
                return;
            }

            _logger.LogWarning(
                "Found {Count} legacy plaintext provider credential(s). " +
                "Encrypting in place with envelope encryption...",
                legacyRows.Count);

            var migrated = 0;
            foreach (var provider in legacyRows)
            {
                try
                {
                    var plaintext = provider.ApiKey!;
                    provider.UpdateCredentials(null, plaintext, vault);
                    migrated++;
                    _logger.LogInformation(
                        "Migrated credential for provider {Code} ({Name}) — sealed in place.",
                        provider.Code, provider.Name);
                }
                catch (Exception ex)
                {
                    // Do NOT abort the whole migration on one bad row — log and
                    // continue. The row is left in its original state.
                    _logger.LogError(ex,
                        "Failed to migrate credential for provider {Code} ({Name}). " +
                        "Row left unchanged — manual recovery required.",
                        provider.Code, provider.Name);
                }
            }

            if (migrated > 0)
            {
                await db.SaveChangesAsync(ct);
                _logger.LogWarning(
                    "Legacy credential migration complete. {Count} provider(s) now have sealed credentials. " +
                    "Future restarts will skip migration (rows are already in v1 format).",
                    migrated);
            }
        }
        catch (Exception ex)
        {
            // Migration failure must not crash the gateway. Log loudly so the
            // operator notices.
            _logger.LogError(ex,
                "Legacy credential migration failed. " +
                "If the gateway refuses to start, check vault configuration and DB connectivity. " +
                "Existing plaintext rows will cause CryptographicException at request time " +
                "until they are migrated (manually via the admin API, or by retrying this service).");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
