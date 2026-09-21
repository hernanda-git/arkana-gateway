using Arkana.Domain.Entities;
using Arkana.Domain.Services;
using Xunit;

namespace Arkana.Infrastructure.Tests.OAuth;

public sealed class GeminiOAuthScopeContractTests
{
    [Fact]
    public void CanonicalContract_IsExactlyLeastPrivilegeScope()
    {
        GeminiOAuthScopeContract.Canonical.Should().Be(
            "https://www.googleapis.com/auth/generative-language.peruserquota");
        GeminiOAuthScopeContract.Canonical.Should().NotContain("cloud-platform");
        GeminiOAuthScopeContract.Canonical.Should().NotContain("generative-language.retriever");
        GeminiOAuthScopeContract.Canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ExistingConfig_CanBeReconciledWithoutChangingCredentialsOrEndpoints()
    {
        var config = OAuthProviderConfig.Create(
            "gemini", "Gemini", OAuthGrant.AuthorizationCode,
            "https://oauth2.googleapis.com/token", "client-id",
            authorizationEndpoint: "https://accounts.google.com/o/oauth2/v2/auth",
            scopes: "https://www.googleapis.com/auth/cloud-platform openid email");

        config.UpdateScopes(GeminiOAuthScopeContract.Canonical);

        config.Scopes.Should().Be(GeminiOAuthScopeContract.Canonical);
        config.ClientId.Should().Be("client-id");
        config.TokenEndpoint.Should().Be("https://oauth2.googleapis.com/token");
        config.AuthorizationEndpoint.Should().Be("https://accounts.google.com/o/oauth2/v2/auth");
    }
}
