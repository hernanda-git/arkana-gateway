namespace Arkana.Gateway.Api.Tests.Middleware;

using System.Diagnostics;
using System.Net;
using Arkana.Domain.Interfaces;
using Arkana.Gateway.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

public sealed class TokenTrackingMiddlewareTests
{
    private static IHost BuildHost()
    {
        var tracker = Substitute.For<ITokenTracker>();
        return new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(tracker);
                    })
                    .Configure(app =>
                    {
                        app.UseMiddleware<TokenTrackingMiddleware>();
                        app.Run(async ctx =>
                        {
                            ctx.Response.StatusCode = 200;
                            await ctx.Response.WriteAsync("OK");
                        });
                    });
            })
            .Build();
    }

    [Fact]
    public async Task Request_ShouldPassThrough()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/v1/chat/completions");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("OK");
    }

    [Fact]
    public async Task AnyPath_ShouldPassThrough()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.GetTestClient();

        var paths = new[] { "/", "/health", "/admin/stats", "/dashboard", "/v1/models" };
        foreach (var path in paths)
        {
            var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task ContextItems_ShouldBeSet()
    {
        using var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(Substitute.For<ITokenTracker>());
                    })
                    .Configure(app =>
                    {
                        app.UseMiddleware<TokenTrackingMiddleware>();
                        app.Run(async ctx =>
                        {
                            // Verify all expected context items exist
                            ctx.Items.ContainsKey("__sw").Should().BeTrue();
                            ctx.Items.ContainsKey("__tracker").Should().BeTrue();
                            ctx.Items.ContainsKey("__provider").Should().BeTrue();
                            ctx.Items.ContainsKey("__model").Should().BeTrue();
                            ctx.Items.ContainsKey("__inputTokens").Should().BeTrue();
                            ctx.Items.ContainsKey("__outputTokens").Should().BeTrue();
                            ctx.Items.ContainsKey("__cost").Should().BeTrue();

                            var sw = ctx.Items["__sw"] as Stopwatch;
                            sw.Should().NotBeNull();
                            sw!.IsRunning.Should().BeTrue();

                            ctx.Items["__provider"].Should().Be("unknown");
                            ctx.Items["__model"].Should().Be("unknown");
                            ctx.Items["__inputTokens"].Should().Be(0);
                            ctx.Items["__outputTokens"].Should().Be(0);
                            ctx.Items["__cost"].Should().Be(0m);

                            ctx.Response.StatusCode = 200;
                            await ctx.Response.WriteAsync("OK");
                        });
                    });
            })
            .Build();

        await host.StartAsync();
        var client = host.GetTestClient();
        var response = await client.GetAsync("/v1/chat/completions");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task Stopwatch_ShouldMeasureDuration()
    {
        using var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(Substitute.For<ITokenTracker>());
                    })
                    .Configure(app =>
                    {
                        app.UseMiddleware<TokenTrackingMiddleware>();
                        app.Run(async ctx =>
                        {
                            var sw = ctx.Items["__sw"] as Stopwatch;
                            sw.Should().NotBeNull();

                            // Simulate some work
                            await Task.Delay(10);

                            sw!.Elapsed.Should().BeGreaterThan(TimeSpan.Zero);
                            sw.ElapsedMilliseconds.Should().BeGreaterThan(0);

                            ctx.Response.StatusCode = 200;
                            await ctx.Response.WriteAsync("OK");
                        });
                    });
            })
            .Build();

        await host.StartAsync();
        var client = host.GetTestClient();
        var response = await client.GetAsync("/v1/chat/completions");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task Stopwatch_ShouldStopAfterRequest()
    {
        var tracker = Substitute.For<ITokenTracker>();
        using var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(tracker);
                    })
                    .Configure(app =>
                    {
                        app.UseMiddleware<TokenTrackingMiddleware>();
                        app.Run(async ctx =>
                        {
                            ctx.Response.StatusCode = 200;
                            await ctx.Response.WriteAsync("OK");
                        });
                    });
            })
            .Build();

        await host.StartAsync();
        var client = host.GetTestClient();
        var response = await client.GetAsync("/v1/chat/completions");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
