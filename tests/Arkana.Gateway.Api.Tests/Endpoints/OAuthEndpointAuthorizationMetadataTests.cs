using Arkana.Domain.Interfaces;
using Arkana.Gateway.Api.Endpoints;
using Arkana.Gateway.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Arkana.Gateway.Api.Tests.Endpoints;

public sealed class OAuthEndpointAuthorizationMetadataTests
{
    [Fact]
    public async Task Management_routes_require_admin_policy_and_callbacks_are_anonymous()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IOAuthProviderConfigRepository>(_ => null!);
        builder.Services.AddSingleton<IOAuthFlowService>(_ => null!);
        builder.Services.AddSingleton<ProviderAccountDashboardFacade>(_ => null!);
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapOAuthEndpoints();
        await app.StartAsync();

        try
        {
            var endpoints = app.Services
                .GetRequiredService<IEnumerable<EndpointDataSource>>()
                .SelectMany(source => source.Endpoints)
                .Where(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is not null)
                .ToDictionary(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName!);

            endpoints["StartOAuth"].Metadata.GetMetadata<IAuthorizeData>()!.Policy
                .Should().Be("OAuthManagement");
            endpoints["DisconnectOAuth"].Metadata.GetMetadata<IAuthorizeData>()!.Policy
                .Should().Be("OAuthManagement");
            endpoints["OAuthCallback"].Metadata.GetMetadata<IAllowAnonymous>()
                .Should().NotBeNull();
            endpoints["RecoverOAuthCallback"].Metadata.GetMetadata<IAllowAnonymous>()
                .Should().NotBeNull();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
