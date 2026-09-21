using Arkana.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Arkana.ServiceDefaults.Tests;

public sealed class ExtensionsTests
{
    [Fact]
    public void AddServiceDefaults_ShouldNotThrow()
    {
        var services = new ServiceCollection();

        var act = () => services.AddServiceDefaults();

        act.Should().NotThrow();
    }

    [Fact]
    public void AddServiceDefaults_ShouldReturnSameServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddServiceDefaults();

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void AddServiceDefaults_ShouldRegisterHealthChecks()
    {
        var services = new ServiceCollection();

        services.AddServiceDefaults();
        var provider = services.BuildServiceProvider();

        var healthCheckService = provider.GetService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>();
        healthCheckService.Should().NotBeNull();
    }

    [Fact]
    public void AddServiceDefaults_ShouldNotThrowWhenCalledMultipleTimes()
    {
        var services = new ServiceCollection();

        var act = () =>
        {
            services.AddServiceDefaults();
            services.AddServiceDefaults();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void AddServiceDefaults_ShouldRegisterOpenTelemetry()
    {
        var services = new ServiceCollection();

        services.AddServiceDefaults();

        // Verify OpenTelemetry TracerProvider is available
        var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetService<OpenTelemetry.Trace.TracerProvider>();
        // TracerProvider may be null if not started, but the service should be registered
        tracerProvider.Should().NotBeNull();
    }

    // ── MapDefaultEndpoints ──────────────────────────────────

    [Fact]
    public void MapDefaultEndpoints_OnWebApplication_ShouldNotThrow()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        var act = () => app.MapDefaultEndpoints();

        act.Should().NotThrow();
    }

    [Fact]
    public void MapDefaultEndpoints_ShouldReturnEndpointConventionBuilder()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        var result = app.MapDefaultEndpoints();

        result.Should().NotBeNull();
    }

    [Fact]
    public void MapDefaultEndpoints_WithServices_ShouldNotThrow()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddServiceDefaults();
        var app = builder.Build();

        var act = () => app.MapDefaultEndpoints();

        act.Should().NotThrow();
    }
}
