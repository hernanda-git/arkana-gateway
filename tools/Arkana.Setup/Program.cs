using Spectre.Console;
using Arkana.Setup.Models;
using Arkana.Setup.Services;
using Arkana.Setup.UI;

AnsiConsole.Profile.Width = Math.Min(AnsiConsole.Profile.Width, 100);

// ── Resolve solution directory ──
var solutionDir = ResolveSolutionDir();
if (solutionDir is null)
{
    AnsiConsole.MarkupLine("[red]Error: Could not find Arkana.slnx. Run this tool from within the Ai-Gateway repository.[/]");
    return 1;
}

// ── Welcome ──
Console.Clear();
WizardRenderer.WelcomeBanner();
AnsiConsole.WriteLine();

// ══════════════════════════════════════════════
//  1.  PLATFORM DETECTION
// ══════════════════════════════════════════════
var detected = PlatformDetector.Detect();
var platform = PlatformDetector.PromptOverride(detected);
var ctx = new SetupContext
{
    Platform = platform,
    SolutionDir = solutionDir
};
AnsiConsole.WriteLine();

// ══════════════════════════════════════════════
//  2.  PREREQUISITES — seamless check → install → validate
// ══════════════════════════════════════════════
AnsiConsole.Write(WizardRenderer.Section("System Prerequisites"));
AnsiConsole.WriteLine();

var report = await WizardRenderer.Spin("Checking prerequisites...", async _ =>
    await PrerequisiteChecker.CheckAllAsync(ctx));

AnsiConsole.WriteLine();
report.RenderTable();
AnsiConsole.WriteLine();

if (report.AnyMissing)
{
    // Show what's needed with a single prompt
    var missingList = string.Join(", ", report.All
        .Where(r => !r.Found)
        .Select(r => r.Label));

    if (WizardRenderer.Confirm($"Install missing prerequisites? [grey]({missingList})[/]"))
    {
        // Batch install with re-check
        report = await DependencyInstaller.InstallAndRecheckAsync(ctx, report);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Final check:[/]");
        report.RenderTable();
        AnsiConsole.WriteLine();

        if (report.AnyMissing)
        {
            var stillMissing = string.Join(", ", report.All
                .Where(r => !r.Found)
                .Select(r => r.Label));
            WizardRenderer.Warn($"Still missing: {stillMissing}");
            if (!WizardRenderer.Confirm("Continue without these? Some features may not work."))
            {
                WizardRenderer.Error("Setup aborted.");
                return 1;
            }
        }
    }
    else
    {
        WizardRenderer.Warn("Skipping dependency installation.");
    }
}
else
{
    WizardRenderer.Success("All prerequisites satisfied!");
}
AnsiConsole.WriteLine();

// ══════════════════════════════════════════════
//  3.  INFRASTRUCTURE (Docker modes only)
// ══════════════════════════════════════════════
if (platform is PlatformKind.Docker or PlatformKind.Wsl)
{
    if (!await DockerManager.StartInfraAsync(ctx))
    {
        if (!WizardRenderer.Confirm("Infrastructure startup had issues. Continue?"))
        {
            WizardRenderer.Error("Setup aborted.");
            return 1;
        }
    }
}

// ══════════════════════════════════════════════
//  4.  CONFIGURATION
// ══════════════════════════════════════════════
await ConfigGenerator.PromptAsync(ctx);

// ══════════════════════════════════════════════
//  5.  DATABASE SETUP
// ══════════════════════════════════════════════
if (!await DatabaseBootstrap.BootstrapAsync(ctx))
{
    if (!WizardRenderer.Confirm("Database setup had issues. Continue?"))
    {
        WizardRenderer.Error("Setup aborted.");
        return 1;
    }
}

// ══════════════════════════════════════════════
//  6.  BUILD & VERIFY
// ══════════════════════════════════════════════
if (!await HealthVerifier.VerifyAsync(ctx))
{
    WizardRenderer.Warn("Build/verify had issues — check output above.");
}

// ══════════════════════════════════════════════
//  SUMMARY
// ══════════════════════════════════════════════
AnsiConsole.WriteLine();
AnsiConsole.Write(new Rule("[bold green]Setup Complete![/]") { Style = Style.Parse("green") });
AnsiConsole.WriteLine();

WizardRenderer.ResultsTable("ARKANA GATEWAY — Setup Summary", new[]
{
    ("Platform", ctx.Platform.ToString()),
    ("Dashboard", $"{ctx.GatewayUrl}"),
    ("API Docs", $"{ctx.GatewayUrl}/scalar/v1"),
    ("Database", $"{ctx.DbHost}:{ctx.DbPort}/{ctx.DbName}"),
    ("Auth", ctx.AuthEnabled ? $"Enabled (user: {ctx.AdminUser})" : "Disabled"),
    ("Build", ctx.MigrationsApplied ? "Passed" : "See above"),
    ("Tests", ctx.HealthCheckPassed ? "Verified" : "Run dotnet test to verify"),
});

AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[bold yellow]Quick Start Commands:[/]");
AnsiConsole.MarkupLine($"  [grey]$[/] [bold]dotnet run --project {Path.Combine(ctx.SolutionDir, "src", "Arkana.Gateway.Api")}[/]");
AnsiConsole.MarkupLine($"  [grey]$[/] [bold]dotnet test[/]  [grey](in solution dir)[/]");
AnsiConsole.WriteLine();

if (ctx.GeneratedApiKey is not null)
{
    AnsiConsole.MarkupLine("[bold yellow]Test your API:[/]");
    AnsiConsole.MarkupLine($"  [grey]$[/] curl -X POST {ctx.GatewayUrl}/v1/chat/completions \\");
    AnsiConsole.MarkupLine($"    [grey]-H \"Authorization: Bearer {ctx.GeneratedApiKey}\" \\[/]");
    AnsiConsole.MarkupLine($"    [grey]-H \"Content-Type: application/json\" \\[/]");
    AnsiConsole.MarkupLine($"    [grey]-d '{{\"model\":\"deepseek-v4-flash\",\"messages\":[{{\"role\":\"user\",\"content\":\"hi\"}}]}}'[/]");
}

AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[dim]Setup performed by ARKANA GATEWAY Setup Wizard v1.0[/]");

return 0;

// ── Helper: walk up from exe location to find .slnx ──
static string? ResolveSolutionDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (dir.GetFiles("Arkana.slnx").Length > 0)
            return dir.FullName;
        dir = dir.Parent;
    }

    dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (dir.GetFiles("Arkana.slnx").Length > 0)
            return dir.FullName;
        dir = dir.Parent;
    }

    return null;
}
