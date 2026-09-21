using Spectre.Console;
using Arkana.Setup.Models;
using Arkana.Setup.UI;

namespace Arkana.Setup.Services;

/// <summary>Pure check — no side effects, no console output. Populates a report.</summary>
public static class PrerequisiteChecker
{
    public static async Task<PrerequisiteReport> CheckAllAsync(SetupContext ctx)
    {
        var report = new PrerequisiteReport();

        await Task.WhenAll(
            CheckDotNetAsync(ctx, report),
            CheckDockerAsync(ctx, report),
            CheckPostgresAsync(ctx, report),
            CheckRedisAsync(ctx, report));

        // Docker-only mode: PostgreSQL + Redis are satisfied by containers
        if (ctx.Platform is PlatformKind.Docker)
        {
            report.Postgres.Found = true;
            report.Postgres.Status = "✓ via Docker containers";
            report.Redis.Found = true;
            report.Redis.Status = "✓ via Docker containers";
        }

        return report;
    }

    private static async Task CheckDotNetAsync(SetupContext ctx, PrerequisiteReport r)
    {
        var (output, code) = await RunAsync("dotnet", "--version");
        if (code == 0 && output.TrimStart().StartsWith("10", StringComparison.Ordinal))
        {
            r.DotNet.Found = true;
            r.DotNet.Status = output.Trim();
            ctx.DotNetSdkFound = true;
        }
        else
        {
            r.DotNet.Found = false;
            r.DotNet.Status = code == 0 ? $"found v{output.Trim()} (need 10.x)" : "not found";
        }
    }

    private static async Task CheckDockerAsync(SetupContext ctx, PrerequisiteReport r)
    {
        var (outDocker, codeDocker) = await RunAsync("docker", "--version");
        if (codeDocker == 0)
        {
            r.Docker.Found = true;
            r.Docker.Status = outDocker.Trim();
            ctx.DockerFound = true;
        }
        else
        {
            r.Docker.Status = "not found";
        }

        var (outCompose, codeCompose) = await RunAsync("docker", "compose version");
        if (codeCompose == 0)
        {
            r.DockerCompose.Found = true;
            r.DockerCompose.Status = outCompose.Trim();
            ctx.DockerComposeFound = true;
        }
        else
        {
            r.DockerCompose.Status = "not found";
        }
    }

    private static async Task CheckPostgresAsync(SetupContext ctx, PrerequisiteReport r)
    {
        var (_, code) = await RunAsync("pg_isready", "-q");
        if (code == 0)
        {
            r.Postgres.Found = true;
            r.Postgres.Status = "accepting connections";
            ctx.PostgresFound = true;
        }
        else
        {
            r.Postgres.Status = "not running / not installed";
        }

        var (_, psqlCode) = await RunAsync("psql", "--version");
        ctx.PsqlFound = psqlCode == 0;
    }

    private static async Task CheckRedisAsync(SetupContext ctx, PrerequisiteReport r)
    {
        var (output, code) = await RunAsync("redis-cli", "ping");
        if (code == 0 && output.Trim() == "PONG")
        {
            r.Redis.Found = true;
            r.Redis.Status = "responding";
            ctx.RedisFound = true;
        }
        else
        {
            r.Redis.Status = "not responding";
        }
    }

    private static async Task<(string Output, int ExitCode)> RunAsync(string command, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = new System.Diagnostics.Process { StartInfo = psi };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            proc.WaitForExit(5000);
            return (output, proc.ExitCode);
        }
        catch
        {
            return ("", -1);
        }
    }
}

/// <summary>Result of a prerequisite check.</summary>
public sealed class PrerequisiteReport
{
    public CheckResult DotNet { get; set; } = new("⚙️ .NET SDK 10+");
    public CheckResult Docker { get; set; } = new("🐳 Docker Engine 24+");
    public CheckResult DockerCompose { get; set; } = new("🐳 Docker Compose 2+");
    public CheckResult Postgres { get; set; } = new("🗄️ PostgreSQL 16");
    public CheckResult Redis { get; set; } = new("📦 Redis 7");

    public bool AllGood => DotNet.Found
        && Docker.Found
        && DockerCompose.Found
        && Postgres.Found
        && Redis.Found;

    public bool AnyMissing => !AllGood;

    public IEnumerable<CheckResult> All => [DotNet, Docker, DockerCompose, Postgres, Redis];

    public void RenderTable()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("Requirement").Centered())
            .AddColumn(new TableColumn("Status").Centered());

        foreach (var r in All)
        {
            var icon = r.Found ? ":check_mark_button:" : ":cross_mark:";
            var color = r.Found ? "green" : "red";
            table.AddRow(
                $"{r.Label}",
                $"[{color}]{icon} {Markup.Escape(r.Status)}[/]");
        }

        AnsiConsole.Write(table);
    }
}

public sealed class CheckResult(string label)
{
    public string Label { get; } = label;
    public bool Found { get; set; }
    public string Status { get; set; } = "pending";
}
