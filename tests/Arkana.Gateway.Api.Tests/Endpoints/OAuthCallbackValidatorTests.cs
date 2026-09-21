using System.Linq;
using Arkana.Gateway.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Arkana.Gateway.Api.Tests.Endpoints;

public sealed class OAuthCallbackValidatorTests
{
    [Theory]
    [InlineData("http://oauth.example.test", false, false)]
    [InlineData("https://oauth.example.test", false, true)]
    [InlineData("http://localhost:51121", true, true)]
    [InlineData("http://localhost:51121", false, false)]
    public void PublicOrigin_RejectsNonLocalHttp_AndAllowsOnlyDevelopmentLoopbackHttp(
        string configuredOrigin, bool isDevelopment, bool expected)
    {
        var context = new DefaultHttpContext();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OAuth:PublicOrigin"] = configuredOrigin
            })
            .Build();

        OAuthEndpoints.TryGetPublicOrigin(context, configuration, isDevelopment, out var origin)
            .Should().Be(expected);
        if (expected)
            origin.Should().Be(configuredOrigin);
    }

    [Theory]
    [InlineData("?state=state-123&code=code-123", true)]
    [InlineData("?state=state-123&error=access_denied", true)]
    [InlineData("?state=state-123", false)]
    [InlineData("?state=state-123&code=code-123&error=access_denied", false)]
    [InlineData("?state=state%200123&code=code-123", false)]
    [InlineData("?state=st%C3%A4te-123&code=code-123", false)]
    [InlineData("?state=state-123&code=%00", false)]
    public void ValidatesCallbackShape(string queryString, bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString(queryString);
        var query = request.Query;

        var actual = OAuthCallbackValidator.TryValidate(
            request,
            query["code"].FirstOrDefault(),
            query["state"].FirstOrDefault(),
            query["error"].FirstOrDefault(),
            out _);

        actual.Should().Be(expected);
    }

    [Fact]
    public void RejectsDuplicateCallbackParameters()
    {
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString("?state=state-123&code=first&code=second");
        var query = request.Query;

        OAuthCallbackValidator.TryValidate(
            request,
            query["code"].FirstOrDefault(),
            query["state"].FirstOrDefault(),
            query["error"].FirstOrDefault(),
            out _).Should().BeFalse();
    }

    [Fact]
    public void RejectsOversizedCode()
    {
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString("?state=state-123&code=" + new string('x', 8193));
        var query = request.Query;

        OAuthCallbackValidator.TryValidate(
            request,
            query["code"].FirstOrDefault(),
            query["state"].FirstOrDefault(),
            query["error"].FirstOrDefault(),
            out _).Should().BeFalse();
    }

    [Fact]
    public void RejectsOversizedError()
    {
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString("?state=state-123&error=" + new string('x', 1025));
        var query = request.Query;

        OAuthCallbackValidator.TryValidate(
            request,
            query["code"].FirstOrDefault(),
            query["state"].FirstOrDefault(),
            query["error"].FirstOrDefault(),
            out _).Should().BeFalse();
    }
}
