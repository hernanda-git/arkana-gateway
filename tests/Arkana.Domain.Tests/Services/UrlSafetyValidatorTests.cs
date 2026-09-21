using System.Net;
using Arkana.Domain.Services;
using FluentAssertions;

namespace Arkana.Domain.Tests.Services;

/// <summary>
/// SSRF defense tests — every entry covers a specific attack class.
/// </summary>
public sealed class UrlSafetyValidatorTests
{
    private static UrlSafetyValidator NewValidator(
        bool allowHttp = false,
        bool allowPrivate = false,
        IReadOnlyList<string>? extraBlocked = null)
    {
        var options = new UrlSafetyOptions
        {
            AllowHttp = allowHttp,
            AllowPrivateAddresses = allowPrivate,
            AdditionalBlockedHosts = extraBlocked
        };
        // DnsResolver is unused by ValidateLiteral but required for the constructor.
        return new UrlSafetyValidator(options, new DnsResolver(TimeSpan.FromSeconds(1)));
    }

    // ── Scheme checks ──────────────────────────────────────────

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com")]
    [InlineData("data:text/plain;base64,aGk=")]
    public void ValidateLiteral_RejectsNonHttpSchemes(string url)
    {
        var sut = NewValidator(allowHttp: true); // http allowed but not ftp
        Action act = () => sut.ValidateLiteral(url);

        act.Should().Throw<UrlSafetyException>()
            .WithMessage("*not allowed*");
    }

    [Fact]
    public void ValidateLiteral_RejectsHttpByDefault()
    {
        var sut = NewValidator();

        Action act = () => sut.ValidateLiteral("http://api.openai.com/v1");

        act.Should().Throw<UrlSafetyException>()
            .WithMessage("*HTTP is not allowed*");
    }

    [Fact]
    public void ValidateLiteral_AllowsHttpWhenEnabled()
    {
        var sut = NewValidator(allowHttp: true);

        var uri = sut.ValidateLiteral("http://api.openai.com/v1");

        uri.Scheme.Should().Be("http");
    }

    [Fact]
    public void ValidateLiteral_AllowsHttps()
    {
        var sut = NewValidator();

        var uri = sut.ValidateLiteral("https://api.openai.com/v1");

        uri.Host.Should().Be("api.openai.com");
    }

    // ── Hostname blocklist (cloud metadata) ─────────────────────

    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://metadata.google.internal/computeMetadata/v1")]
    [InlineData("https://metadata.azure.com/")]
    [InlineData("https://100.100.100.200/latest/meta-data")]
    [InlineData("https://169.254.170.2/v3/")]
    public void ValidateLiteral_BlocksCloudMetadataHosts(string url)
    {
        var sut = NewValidator(allowHttp: true, allowPrivate: true);

        Action act = () => sut.ValidateLiteral(url);

        act.Should().Throw<UrlSafetyException>()
            .WithMessage("*blocklist*");
    }

    [Fact]
    public void ValidateLiteral_BlocksLoopbackHost()
    {
        var sut = NewValidator(allowHttp: true);

        Action act = () => sut.ValidateLiteral("http://localhost:5432/");

        act.Should().Throw<UrlSafetyException>()
            .WithMessage("*blocklist*");
    }

    [Fact]
    public void ValidateLiteral_BlocksOperatorConfiguredHost()
    {
        var blocked = new[] { "evil.example.com" };
        var sut = NewValidator(allowHttp: true, extraBlocked: blocked);

        Action act = () => sut.ValidateLiteral("https://evil.example.com/v1");

        act.Should().Throw<UrlSafetyException>()
            .WithMessage("*operator blocklist*");
    }

    // ── IP literal ranges ──────────────────────────────────────

    [Theory]
    [InlineData("https://10.0.0.1/v1")]               // private
    [InlineData("https://172.16.5.5/v1")]            // private
    [InlineData("https://172.31.255.255/v1")]        // private boundary
    [InlineData("https://192.168.1.1/v1")]           // private
    [InlineData("https://127.0.0.1/v1")]             // loopback literal
    [InlineData("https://100.64.0.1/v1")]            // CGNAT
    [InlineData("https://224.0.0.1/v1")]             // multicast
    [InlineData("https://255.255.255.255/v1")]       // broadcast
    public void ValidateLiteral_BlocksDisallowedIpLiterals(string url)
    {
        var sut = NewValidator(allowHttp: true, allowPrivate: false);

        Action act = () => sut.ValidateLiteral(url);

        act.Should().Throw<UrlSafetyException>();
    }

    // 169.254.169.254 and 0.0.0.0 are caught by the hostname blocklist before
    // the IP-range check; verify they're blocked with the right category.
    [Theory]
    [InlineData("https://169.254.169.254/v1", "*blocklist*")]   // cloud metadata hostname
    [InlineData("https://0.0.0.0/v1", "*blocklist*")]          // "this network"
    public void ValidateLiteral_BlocksSpecialHostsWithBlocklistMessage(string url, string expectedFragment)
    {
        var sut = NewValidator(allowHttp: true, allowPrivate: false);

        Action act = () => sut.ValidateLiteral(url);

        act.Should().Throw<UrlSafetyException>()
            .WithMessage(expectedFragment);
    }

    [Fact]
    public void ValidateLiteral_AllowsPrivateIpWhenOptedIn()
    {
        var sut = NewValidator(allowHttp: true, allowPrivate: true);

        var uri = sut.ValidateLiteral("https://10.0.0.1/v1");

        uri.Host.Should().Be("10.0.0.1");
    }

    [Fact]
    public void ValidateLiteral_AllowsPublicIpLiteral()
    {
        var sut = NewValidator(allowHttp: true);

        var uri = sut.ValidateLiteral("https://8.8.8.8/v1");

        uri.Host.Should().Be("8.8.8.8");
    }

    // ── Empty / malformed ──────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("https://")]  // missing host
    public void ValidateLiteral_RejectsEmptyOrInvalid(string url)
    {
        var sut = NewValidator();

        Action act = () => sut.ValidateLiteral(url);

        act.Should().Throw<UrlSafetyException>();
    }

    // ── DNS resolution path (async) ────────────────────────────

    [Fact]
    public async Task ValidateAsync_ResolvesHostBeforeChecking()
    {
        // Use a hostname that resolves to a public IP (1.1.1.1) — should pass.
        var sut = NewValidator();

        var uri = await sut.ValidateAsync("https://one.one.one.one/v1");

        uri.Host.Should().Be("one.one.one.one");
    }

    [Fact]
    public async Task ValidateAsync_RejectsHostResolvingToPrivateIp()
    {
        // localhost resolves to 127.0.0.1 — must be blocked even when not literal.
        // (We also have an explicit blocklist for "localhost" but DNS check is the
        // deeper defense.)
        var sut = NewValidator(allowPrivate: false);

        Func<Task> act = async () => await sut.ValidateAsync("http://127.0.0.1.xip.io/");

        await act.Should().ThrowAsync<UrlSafetyException>();
    }

    [Fact]
    public async Task ValidateAsync_RejectsUnknownHost()
    {
        // A DNS lookup that should fail
        var sut = NewValidator();

        Func<Task> act = async () =>
            await sut.ValidateAsync("https://this-host-definitely-does-not-exist-abc123.invalid/v1");

        await act.Should().ThrowAsync<UrlSafetyException>()
            .WithMessage("*DNS*");
    }

    // ── DNS cache behavior ─────────────────────────────────────

    [Fact]
    public async Task ResolveAll_CachesResults()
    {
        var dns = new DnsResolver(TimeSpan.FromSeconds(2));
        var first = await dns.ResolveAllAsync("one.one.one.one");
        var second = await dns.ResolveAllAsync("one.one.one.one");

        first.Should().BeEquivalentTo(second);
        first.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ResolveAll_HandlesIpLiteral()
    {
        var dns = new DnsResolver();

        var addresses = await dns.ResolveAllAsync("8.8.8.8");

        addresses.Should().ContainSingle()
            .Which.Should().Be(IPAddress.Parse("8.8.8.8"));
    }
}
