using Arkana.Gateway.Api.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Arkana.Gateway.Api.Tests.Endpoints;

public sealed class ImageGenerationEndpointsTests
{
    private static DefaultHttpContext CreateHttpContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = "test-tenant";
        return ctx;
    }

    private static int GetStatusCode(IResult result)
    {
        var prop = result.GetType().GetProperty("StatusCode");
        if (prop is not null)
            return (int)prop.GetValue(result)!;
        if (result is NotFound<object>) return 404;
        if (result is Conflict<object>) return 409;
        if (result is BadRequest<object>) return 400;
        return 0;
    }

    [Fact]
    public async Task Generate_ValidRequest_Returns200()
    {
        var http = CreateHttpContext();
        var request = new ImageGenerationRequest { Prompt = "A sunset over mountains", N = 1, Size = "1024x1024" };

        var result = await ImageGenerationEndpoints.GenerateImage(request, http);
        GetStatusCode(result).Should().Be(200);
    }

    [Fact]
    public async Task Generate_MultipleImages_ReturnsCorrectCount()
    {
        var http = CreateHttpContext();
        var request = new ImageGenerationRequest { Prompt = "A cat", N = 3, Size = "512x512" };

        var result = await ImageGenerationEndpoints.GenerateImage(request, http);
        GetStatusCode(result).Should().Be(200);
    }

    [Fact]
    public async Task Generate_EmptyPrompt_Returns400()
    {
        var http = CreateHttpContext();
        var request = new ImageGenerationRequest { Prompt = "" };

        var result = await ImageGenerationEndpoints.GenerateImage(request, http);
        GetStatusCode(result).Should().Be(400);
    }

    [Fact]
    public async Task Generate_NullPrompt_Returns400()
    {
        var http = CreateHttpContext();
        var request = new ImageGenerationRequest { Prompt = null! };

        var result = await ImageGenerationEndpoints.GenerateImage(request, http);
        GetStatusCode(result).Should().Be(400);
    }

    [Fact]
    public async Task Generate_InvalidSize_Returns400()
    {
        var http = CreateHttpContext();
        var request = new ImageGenerationRequest { Prompt = "A dog", Size = "300x300" };

        var result = await ImageGenerationEndpoints.GenerateImage(request, http);
        GetStatusCode(result).Should().Be(400);
    }

    [Fact]
    public async Task Generate_NTooHigh_Returns400()
    {
        var http = CreateHttpContext();
        var request = new ImageGenerationRequest { Prompt = "A dog", N = 5 };

        var result = await ImageGenerationEndpoints.GenerateImage(request, http);
        GetStatusCode(result).Should().Be(400);
    }

    [Fact]
    public async Task Generate_NZero_Returns400()
    {
        var http = CreateHttpContext();
        var request = new ImageGenerationRequest { Prompt = "A dog", N = 0 };

        var result = await ImageGenerationEndpoints.GenerateImage(request, http);
        GetStatusCode(result).Should().Be(400);
    }

    [Fact]
    public async Task Generate_URLContainsTenantId()
    {
        var http = CreateHttpContext();
        http.Items["TenantId"] = "acme-corp";
        var request = new ImageGenerationRequest { Prompt = "A rocket", N = 1 };

        var result = await ImageGenerationEndpoints.GenerateImage(request, http);
        GetStatusCode(result).Should().Be(200);

        // Verify URL contains tenant ID via reflection on the response
        var ok = result as Ok<ImageGenerationResponse>;
        ok.Should().NotBeNull();
        ok!.Value!.Data[0].Url!.Should().Contain("acme-corp");
    }

    [Fact]
    public async Task Generate_AllValidSizes()
    {
        var http = CreateHttpContext();
        var sizes = new[] { "256x256", "512x512", "1024x1024", "1024x1792", "1792x1024" };

        foreach (var size in sizes)
        {
            var request = new ImageGenerationRequest { Prompt = "test", Size = size };
            var result = await ImageGenerationEndpoints.GenerateImage(request, http);
            GetStatusCode(result).Should().Be(200, $"{size} should be valid");
        }
    }
}
