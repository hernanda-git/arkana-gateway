using Arkana.Domain.Entities;
using Arkana.Domain.Services;
using FluentAssertions;

namespace Arkana.Domain.Tests.Entities;

public sealed class AiProviderTests
{
    private static UrlSafetyValidator AllowAllValidator() =>
        new(new UrlSafetyOptions { AllowHttp = true, AllowPrivateAddresses = true },
            new DnsResolver(TimeSpan.FromMilliseconds(100)));
    [Fact]
    public void Create_SetsAllPropertiesCorrectly()
    {
        // SECURITY: when a vault is supplied, the ApiKey is sealed — not stored
        // as plaintext. The decryption round-trip is the contract being tested.
        var vault = TestVault.Create();
        var provider = AiProvider.Create(
            "OpenAI", "OpenAI", 1,
            "https://api.openai.com", "sk-123",
            vault: vault,
            costPerInput: 0.01m, costPerOutput: 0.03m);

        provider.Name.Should().Be("OpenAI");
        provider.Code.Should().Be("openai");
        provider.Priority.Should().Be(1);
        provider.BaseUrl.Should().Be("https://api.openai.com");
        provider.HasCredential.Should().BeTrue();
        provider.DecryptApiKey(vault).Should().Be("sk-123");
        provider.CostPerInputToken.Should().Be(0.01m);
        provider.CostPerOutputToken.Should().Be(0.03m);
        provider.IsEnabled.Should().BeTrue();
        provider.Id.Should().NotBeEmpty();
        provider.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Create_LowercasesTheCode()
    {
        var provider = AiProvider.Create("OpenAI", "OPENAI", 1);

        provider.Code.Should().Be("openai");
    }

    [Fact]
    public void Enable_SetsIsEnabledToTrue()
    {
        var provider = AiProvider.Create("Provider", "prov", 1);
        provider.Disable();

        provider.Enable();

        provider.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Disable_SetsIsEnabledToFalse()
    {
        var provider = AiProvider.Create("Provider", "prov", 1);

        provider.Disable();

        provider.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void UpdateCredentials_UpdatesBothBaseUrlAndApiKey()
    {
        // SECURITY: with a vault, the stored ApiKey is the sealed form, not plaintext.
        // Both vault and validator are required to exercise the full credential + URL path.
        var vault = TestVault.Create();
        var validator = AllowAllValidator();
        var provider = AiProvider.Create("Provider", "prov", 1,
            "https://old.url", "old-key", vault, validator);

        provider.UpdateCredentials("https://new.url", "new-key", vault, validator);

        provider.BaseUrl.Should().Be("https://new.url");
        // The stored value is the sealed form (test double uses "testv1:" prefix).
        provider.ApiKey.Should().StartWith("testv1:");
        provider.DecryptApiKey(vault).Should().Be("new-key");
    }

    [Fact]
    public void UpdateCredentials_PreservesExistingValuesWhenNullPassed()
    {
        var vault = TestVault.Create();
        var validator = AllowAllValidator();
        var provider = AiProvider.Create("Provider", "prov", 1,
            "https://existing.url", "existing-key", vault, validator);

        var originalSealed = provider.ApiKey;
        provider.UpdateCredentials(null, null, vault, validator);

        provider.BaseUrl.Should().Be("https://existing.url");
        // Sealed value is preserved unchanged when no new credentials are provided.
        provider.ApiKey.Should().Be(originalSealed);
        provider.DecryptApiKey(vault).Should().Be("existing-key");
    }

    // ── SSRF defense tests ───────────────────────────────────────

    [Fact]
    public void Create_WithValidator_RejectsCloudMetadataUrl()
    {
        // Use the production-like validator (HTTPS-only, no private IPs)
        var validator = new UrlSafetyValidator(
            new UrlSafetyOptions { AllowHttp = false, AllowPrivateAddresses = false },
            new DnsResolver(TimeSpan.FromMilliseconds(100)));

        Action act = () => AiProvider.Create("Bad", "bad", 1, baseUrl: "https://169.254.169.254/", validator: validator);

        act.Should().Throw<UrlSafetyException>();
    }

    [Fact]
    public void Create_WithoutValidator_AcceptsAnyUrl_LegacyBehavior()
    {
        // Null validator is still allowed for backward compatibility with
        // existing callers / tests that don't care about SSRF.
        var provider = AiProvider.Create("Legacy", "legacy", 1,
            baseUrl: "https://anything.example.com");

        provider.BaseUrl.Should().Be("https://anything.example.com");
    }

    [Fact]
    public void Create_WithValidator_AcceptsValidHttpsUrl()
    {
        var validator = AllowAllValidator();

        var provider = AiProvider.Create("OpenAI", "openai", 1,
            baseUrl: "https://api.openai.com/v1", validator: validator);

        provider.BaseUrl.Should().Be("https://api.openai.com/v1");
    }

    [Fact]
    public void UpdateCredentials_WithValidator_RejectsMetadataUrl()
    {
        var validator = AllowAllValidator();
        var provider = AiProvider.Create("Provider", "prov", 1,
            baseUrl: "https://api.openai.com", validator: validator);

        // Pass null vault and null apiKeyPlaintext — we're testing URL validation,
        // not credential sealing. The validator must reject the URL before the
        // vault would be consulted.
        Action act = () => provider.UpdateCredentials("https://169.254.169.254", null, null, validator);

        act.Should().Throw<UrlSafetyException>();
    }

    [Fact]
    public void UpdateCredentials_WithNullValidator_ThrowsToForceOptIn()
    {
        var provider = AiProvider.Create("Provider", "prov", 1);

        // Pass null vault AND null apiKeyPlaintext so only the URL-validator
        // check fires — the no-vault check is for plaintext-credential handling.
        Action act = () => provider.UpdateCredentials("https://new.url", null, null, validator: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*UrlSafetyValidator*");
    }
}
