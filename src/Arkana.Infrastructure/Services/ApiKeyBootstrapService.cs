using Arkana.Domain.Interfaces;
using Arkana.Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.Services;

/// <summary>
/// On startup, seals the OpenCode provider's API key from
/// the environment variable into the database (if no key is already stored).
///
/// The vault is resolved from DI. If no env var is set and no DB key exists,
/// the provider is left in its seeded state (null key).
///
/// SECURITY: The plaintext env var is sealed by the vault before being
/// persisted. The entity property never holds plaintext.
/// </summary>
internal sealed class ApiKeyBootstrapService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ApiKeyBootstrapService> _logger;

    public ApiKeyBootstrapService(IServiceProvider services, ILogger<ApiKeyBootstrapService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IAiProviderRepository>();
            var vault = scope.ServiceProvider.GetRequiredService<ICredentialVault>();

            var providers = await repo.GetAllAsync(ct);

            // Bootstrap OpenCode provider (carries DeepSeek + GLM + Kimi + MiMo models)
            var openCode = providers.FirstOrDefault(p =>
                p.Code.Equals("opencode", StringComparison.OrdinalIgnoreCase));

            if (openCode is not null && !openCode.HasCredential)
            {
                var envKey = Environment.GetEnvironmentVariable("OPENCODE_GO_API_KEY");
                if (!string.IsNullOrEmpty(envKey))
                {
                    // SECURITY: vault.Seal() converts plaintext to the v1 envelope
                    // format before the value is written to the entity property.
                    openCode.UpdateCredentials(null, envKey, vault);
                    await repo.UpdateAsync(openCode, ct);
                    _logger.LogInformation(
                        "Bootstrapped OpenCode API key from OPENCODE_GO_API_KEY env var into database (sealed).");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to bootstrap API key from environment. " +
                "DB may not be ready yet, or vault is misconfigured. " +
                "Set keys via the PUT admin providers apikey endpoint.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
