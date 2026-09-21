using System.Net;
using Arkana.Domain.Services;
using Arkana.Infrastructure.AI;
using FluentAssertions;

namespace Arkana.Infrastructure.Tests.AI;

public sealed class SsrfSafeHttpHandlerTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            });
        }
    }

    private static SsrfSafeHttpHandler BuildHandler(
        CountingHandler inner,
        bool allowHttp = true,
        bool allowPrivate = true)
    {
        var options = new UrlSafetyOptions
        {
            AllowHttp = allowHttp,
            AllowPrivateAddresses = allowPrivate
        };
        var validator = new UrlSafetyValidator(options, new DnsResolver(TimeSpan.FromMilliseconds(100)));
        var guard = new SsrfSafeHttpHandler(validator) { InnerHandler = inner };
        return guard;
    }

    [Fact]
    public async Task AllowsRequestsToPublicHttps()
    {
        var inner = new CountingHandler();
        using var guard = BuildHandler(inner);

        using var client = new HttpClient(guard);
        // Use IP literal 1.1.1.1 (Cloudflare DNS) — no DNS resolution needed,
        // avoiding flake in offline CI environments.
        using var response = await client.GetAsync("https://1.1.1.1/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task BlocksRequestsToCloudMetadata()
    {
        var inner = new CountingHandler();
        using var guard = BuildHandler(inner, allowHttp: true, allowPrivate: false);

        using var client = new HttpClient(guard);
        Func<Task> act = async () => await client.GetAsync("https://169.254.169.254/latest/meta-data");

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*SSRF*");
        inner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task BlocksHttpWhenNotAllowed()
    {
        var inner = new CountingHandler();
        using var guard = BuildHandler(inner, allowHttp: false, allowPrivate: false);

        using var client = new HttpClient(guard);
        Func<Task> act = async () => await client.GetAsync("http://1.1.1.1/v1/models");

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task DisabledHandler_PassesThrough()
    {
        var inner = new CountingHandler();
        var options = new UrlSafetyOptions { AllowHttp = false, AllowPrivateAddresses = false };
        var validator = new UrlSafetyValidator(options, new DnsResolver(TimeSpan.FromMilliseconds(100)));
        using var guard = new SsrfSafeHttpHandler(validator, enabled: false) { InnerHandler = inner };

        using var client = new HttpClient(guard);
        using var response = await client.GetAsync("http://169.254.169.254/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.CallCount.Should().Be(1);
    }
}
