using System.Security.Claims;

namespace Arkana.Gateway.Api.Auth;

/// <summary>
/// Claims helpers for the Google login principal.
/// </summary>
public static class GoogleClaims
{
    /// <summary>
    /// True when the principal was established by the Google OAuth login handler
    /// (carries <c>LoginProvider=Google</c>). Password-admin sessions do not have
    /// this claim and therefore bypass the Google single-page scope guard.
    /// </summary>
    public static bool IsGoogleLoginUser(this ClaimsPrincipal principal) =>
        principal?.HasClaim(c => c.Type == "LoginProvider" && c.Value == "Google") == true;
}
