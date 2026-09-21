using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arkana.Infrastructure.OAuth;

/// <summary>
/// Background refresher: proactively rotates OAuth access tokens that are within
/// 5 minutes of expiry so outbound requests never stall on a refresh. Fails safe
/// per-row and never throws out of the loop.
/// </summary>
public sealed class OAuthTokenRefreshService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OAuthTokenRefreshService> _log;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(5);

    public OAuthTokenRefreshService(IServiceScopeFactory scopes, ILogger<OAuthTokenRefreshService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        // Small initial delay so startup work settles.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RefreshDueTokensAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "OAuth refresh sweep failed (will retry next interval).");
            }
        }
    }

    private async Task RefreshDueTokensAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IProviderOAuthTokenRepository>();
        var due = await tokens.GetConnectedNeedingRefreshAsync(Slack, ct);
        foreach (var t in due)
        {
            try
            {
                var flow = scope.ServiceProvider.GetRequiredService<IOAuthFlowService>();
                // Touching GetValidAccessTokenAsync triggers an in-place refresh.
                await flow.GetValidAccessTokenForTenantAsync(t.AiProviderId, t.TenantId, Slack, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to refresh OAuth token for provider {Provider}", t.AiProviderId);
            }
        }
    }
}
