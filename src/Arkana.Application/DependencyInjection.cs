using Arkana.Application.Features.Chat;
using Arkana.Domain.Services;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Arkana.Application;

/// <summary>
/// Configures application-layer services including MediatR and FluentValidation.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers MediatR handlers, FluentValidation validators, and the
    /// fallback-chain executor from the application assembly.
    /// </summary>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly()));
        services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());

        // Fallback chain executor (REL-ARKANA-001) — wraps primary chat
        // completion calls and walks lower-priority providers on failure.
        // Includes retry-with-backoff (REL-ARKANA-002) and account-level
        // cooldown (REL-ARKANA-003). The cooldown tracker is in-memory and
        // singleton — per gateway instance. Multi-instance deployments
        // need a Redis-backed implementation (Phase 2 / PERF).
        // Default 5 failures/60s → 30s cooldown. Bound to a singleton
        // so both the executor and any direct consumers see the same
        // configuration instance.
        services.AddSingleton(_ => CooldownOptions.Default);
        services.AddSingleton<CooldownTracker>();
        services.AddSingleton<FallbackChainExecutor>();

        // Response cache options (PERF-ARKANA-002) — bound from
        // configuration. The handler does the read-through; the
        // in-memory implementation is registered in the Infrastructure
        // layer (it depends on the DI assembly's logging + options
        // surface there).
        services.AddOptions<ResponseCacheOptions>()
            .BindConfiguration(ResponseCacheOptions.Section);

        return services;
    }
}
