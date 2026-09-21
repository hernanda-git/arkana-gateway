using System.Globalization;
namespace Arkana.Setup.Models;

/// <summary>Detection and user-preference for the target OS / runtime environment.</summary>
public enum PlatformKind
{
    Ubuntu,
    Windows,
    MacOS,
    Docker,
    Wsl
}

/// <summary>Carries all state accumulated during the setup wizard.</summary>
public sealed class SetupContext
{
    public PlatformKind Platform { get; set; }

    // ── Prerequisite state ──
    public bool DotNetSdkFound { get; set; }
    public bool DockerFound { get; set; }
    public bool DockerComposeFound { get; set; }
    public bool PostgresFound { get; set; }
    public bool RedisFound { get; set; }
    public bool PsqlFound { get; set; }

    // ── Postgres config ──
    public string DbHost { get; set; } = "localhost";
    public int DbPort { get; set; } = 5432;
    public string DbName { get; set; } = "arkana";
    public string DbUser { get; set; } = "arkana";
    public string DbPassword { get; set; } = "";

    // ── Provider API keys ──
    public string OpenCodeApiKey { get; set; } = "";
    public string? OpenAiApiKey { get; set; }
    public string? AnthropicApiKey { get; set; }
    public string? GeminiApiKey { get; set; }

    // ── Admin credentials ──
    public bool AuthEnabled { get; set; }
    public string AdminUser { get; set; } = "admin";
    public string AdminPassword { get; set; } = "";

    // ── Paths ──
    public string SolutionDir { get; set; } = "";
    public string DeployDir => Path.Combine(SolutionDir, "deploy");

    // ── Results ──
    public string? GatewayUrl { get; set; } = "http://localhost:5011";
    public string? GeneratedApiKey { get; set; }
    public bool MigrationsApplied { get; set; }
    public bool SeedCompleted { get; set; }
    public bool HealthCheckPassed { get; set; }

    public string ConnectionString =>
        $"Host={EscapeConnStr(DbHost)};Port={DbPort};Database={EscapeConnStr(DbName)};Username={EscapeConnStr(DbUser)};Password={EscapeConnStr(DbPassword)}";

    /// <summary>Percent-encode connection-string-sensitive characters in a value.</summary>
    private static string EscapeConnStr(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is ';' or '\\' or '\'' or '"' or '=' or ' ' or '%')
                sb.AppendFormat(CultureInfo.InvariantCulture, "%{0:X2}", (int)c);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }
}
