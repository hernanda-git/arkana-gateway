namespace Arkana.Domain.Services;

/// <summary>
/// Dashboard authentication config. Toggle via appsettings or env var.
/// <c>AUTH=0</c> or <c>Auth:Enabled=false</c> bypasses all dashboard auth.
/// </summary>
public sealed class AuthConfig
{
    public const string Section = "Auth";

    /// <summary>Master switch. false = no auth required on any dashboard page.</summary>
    public bool Enabled { get; set; }

    /// <summary>Minutes before session expires. Default 8 hours.</summary>
    public int SessionTimeoutMinutes { get; set; } = 480;
}
