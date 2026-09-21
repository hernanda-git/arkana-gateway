using System.Reflection;
using Arkana.Gateway.Api.Endpoints;

namespace Arkana.Gateway.Api.Tests.Endpoints;

public sealed class ResponsesEndpointErrorPolicyTests
{
    private static string Sanitize(int statusCode, string? providerCode)
    {
        var method = typeof(ResponsesEndpoints).GetMethod(
            "SanitizedUpstreamError",
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (string)method!.Invoke(null, [statusCode, providerCode])!;
    }

    [Theory]
    [InlineData(401, "opencode", "OpenCode authentication failed.")]
    [InlineData(403, "openrouter", "OpenRouter authorization failed.")]
    [InlineData(429, "ollama", "Ollama rate limit exceeded.")]
    [InlineData(503, "chatgpt-acc1", "ChatGPT service is unavailable.")]
    [InlineData(401, "gemini-acc1", "Gemini authentication failed.")]
    [InlineData(400, "custom-provider", "Upstream provider request failed (HTTP 400).")]
    public void Uses_fixed_provider_family_message(int statusCode, string providerCode, string expected)
    {
        Sanitize(statusCode, providerCode).Should().Be(expected);
    }

    [Fact]
    public void Never_includes_provider_controlled_diagnostic_text()
    {
        var result = Sanitize(401, "opencode\nBearer fake-token upstream-body");

        result.Should().Be("Upstream provider authentication failed.");
        result.Should().NotContain("fake-token");
        result.Should().NotContain("upstream-body");
    }
}
