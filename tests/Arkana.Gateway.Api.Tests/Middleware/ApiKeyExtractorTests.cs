namespace Arkana.Gateway.Api.Tests.Middleware;

using Arkana.Gateway.Api.Middleware;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Covers the two defects in the inline credential parsing this helper
/// replaced (see <see cref="ApiKeyExtractor"/> remarks).
/// </summary>
public sealed class ApiKeyExtractorTests
{
    private static HttpRequest RequestWith(params (string Name, string Value)[] headers)
    {
        var ctx = new DefaultHttpContext();
        foreach (var (name, value) in headers) ctx.Request.Headers[name] = value;
        return ctx.Request;
    }

    [Fact]
    public void XApiKeyHeader_ShouldBeExtracted()
    {
        var request = RequestWith(("X-Api-Key", "arkana-abc123"));

        ApiKeyExtractor.TryExtract(request, out var key).Should().BeTrue();
        key.Should().Be("arkana-abc123");
    }

    [Fact]
    public void BearerToken_ShouldBeExtracted()
    {
        var request = RequestWith(("Authorization", "Bearer arkana-abc123"));

        ApiKeyExtractor.TryExtract(request, out var key).Should().BeTrue();
        key.Should().Be("arkana-abc123");
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("BEARER")]
    [InlineData("BeArEr")]
    public void BearerScheme_ShouldBeCaseInsensitive(string scheme)
    {
        // RFC 6750 makes the scheme token case-insensitive. The old
        // .Replace("Bearer ", "") only matched the exact casing, so a
        // lowercase scheme left the literal "bearer …" as the key and the
        // caller got a 401 with a perfectly valid credential.
        var request = RequestWith(("Authorization", $"{scheme} arkana-abc123"));

        ApiKeyExtractor.TryExtract(request, out var key).Should().BeTrue();
        key.Should().Be("arkana-abc123");
    }

    [Fact]
    public void KeyContainingSchemeText_ShouldNotBeCorrupted()
    {
        // The old .Replace() stripped EVERY occurrence, not just the prefix,
        // silently mangling any key containing the substring "Bearer ".
        var request = RequestWith(("X-Api-Key", "arkana-Bearer separator"));

        ApiKeyExtractor.TryExtract(request, out var key).Should().BeTrue();
        key.Should().Be("arkana-Bearer separator");
    }

    [Fact]
    public void XApiKey_ShouldTakePrecedenceOverAuthorization()
    {
        var request = RequestWith(
            ("X-Api-Key", "from-x-api-key"),
            ("Authorization", "Bearer from-authorization"));

        ApiKeyExtractor.TryExtract(request, out var key).Should().BeTrue();
        key.Should().Be("from-x-api-key");
    }

    [Fact]
    public void SurroundingWhitespace_ShouldBeTrimmed()
    {
        var request = RequestWith(("Authorization", "Bearer   arkana-abc123  "));

        ApiKeyExtractor.TryExtract(request, out var key).Should().BeTrue();
        key.Should().Be("arkana-abc123");
    }

    [Fact]
    public void NoCredential_ShouldReturnFalse()
    {
        ApiKeyExtractor.TryExtract(RequestWith(), out var key).Should().BeFalse();
        key.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public void MalformedOrNonBearerAuthorization_ShouldReturnFalse(string header)
    {
        var request = RequestWith(("Authorization", header));

        ApiKeyExtractor.TryExtract(request, out _).Should().BeFalse();
    }

    [Fact]
    public void Extract_ShouldReturnNull_WhenAbsent()
    {
        ApiKeyExtractor.Extract(RequestWith()).Should().BeNull();
    }
}
