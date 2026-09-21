namespace Arkana.Gateway.Api.Auth;

/// <summary>
/// Configuration for Google OAuth 2.0 dashboard login (Authorization Code + PKCE).
/// Bound from the "GoogleLogin" configuration section. Env vars use the
/// ASP.NET double-underscore nesting convention:
///   GoogleLogin__Enabled, GoogleLogin__ClientId, GoogleLogin__ClientSecret,
///   GoogleLogin__AllowedDomains, GoogleLogin__DefaultRole, GoogleLogin__AdminEmails.
/// </summary>
public sealed class GoogleLoginOptions
{
    public const string Section = "GoogleLogin";

    /// <summary>Master switch. false = the "Sign in with Google" button and the
    /// /auth/google/login endpoint are disabled (404). Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Google Cloud OAuth client id (Web application type).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Google Cloud OAuth client secret.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Comma/semicolon-separated allowed email domains. EMPTY = any Google
    /// account is accepted (public). When set, only matching domains pass.
    /// </summary>
    public string AllowedDomains { get; set; } = string.Empty;

    /// <summary>Role assigned to Google users by default. Default "User".</summary>
    public string DefaultRole { get; set; } = "User";

    /// <summary>
    /// Comma/semicolon-separated Google email addresses that are granted the
    /// Admin role (overrides DefaultRole).
    /// </summary>
    public string AdminEmails { get; set; } = string.Empty;

    /// <summary>
    /// Default tenant Google (and the seeded admin) users are scoped to.
    /// Mirrors the constant in AdminUserSeeder.
    /// </summary>
    public static readonly Guid DefaultTenantId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>Parsed allowed domains (trimmed, lowercased, empty = open).</summary>
    public IReadOnlyList<string> ParsedAllowedDomains() =>
        string.IsNullOrWhiteSpace(AllowedDomains)
            ? Array.Empty<string>()
            : AllowedDomains.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(d => d.ToLowerInvariant())
                .ToList();

    /// <summary>Parsed admin emails (trimmed, lowercased).</summary>
    public IReadOnlyList<string> ParsedAdminEmails() =>
        string.IsNullOrWhiteSpace(AdminEmails)
            ? Array.Empty<string>()
            : AdminEmails.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.ToLowerInvariant())
                .ToList();
}
