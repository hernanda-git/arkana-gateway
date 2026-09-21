using Arkana.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkana.Gateway.Api.Services;

/// <summary>
/// Startup service that runs migrations and seeders before the gateway is
/// considered ready. A migration/seed failure is fatal so health cannot report
/// a usable release against an uninitialized database.
/// </summary>
public sealed class AdminUserSeederHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AdminUserSeederHostedService> _logger;

    public AdminUserSeederHostedService(IServiceProvider services, ILogger<AdminUserSeederHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("AdminUserSeeder: starting background seed...");
            await AdminUserSeeder.SeedAsync(_services);
            await OAuthConfigSeeder.SeedAsync(_services);
            _logger.LogInformation("AdminUserSeeder: completed successfully.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogCritical(ex, "AdminUserSeeder: migration or seed failed; startup is aborted.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
