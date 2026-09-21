using Spectre.Console;
using Arkana.Setup.Models;
using Arkana.Setup.UI;

namespace Arkana.Setup.Services;

/// <summary>Starts infrastructure via Docker Compose (PostgreSQL, Redis, Qdrant).</summary>
public static class DockerManager
{
    public static async Task<bool> StartInfraAsync(SetupContext ctx)
    {
        AnsiConsole.Write(WizardRenderer.Section("Starting Infrastructure (Docker)"));
        AnsiConsole.WriteLine();

        var composeFile = Path.Combine(ctx.DeployDir, "docker-compose.yml");
        if (!File.Exists(composeFile))
        {
            WizardRenderer.Error($"docker-compose.yml not found at {composeFile}");
            return false;
        }

        return await AnsiConsole.Status()
            .StartAsync("Starting PostgreSQL, Redis, Qdrant...", async _ =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("docker", "compose -f \"" + composeFile + "\" up -d")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = ctx.DeployDir
                    };

                    var proc = System.Diagnostics.Process.Start(psi)!;
                    var output = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync();

                    if (proc.ExitCode != 0)
                    {
                        var err = await proc.StandardError.ReadToEndAsync();
                        WizardRenderer.Error(err.Trim()[..Math.Min(err.Trim().Length, 300)]);
                        return false;
                    }

                    WizardRenderer.Success("Containers started");

                    // Wait for PostgreSQL to be healthy (with progress feedback)
                    await AnsiConsole.Status()
                        .StartAsync("Waiting for PostgreSQL health check (up to 60s)...", async ctx2 =>
                        {
                            for (int i = 0; i < 30; i++)
                            {
                                try
                                {
                                    var (outp, code) = await RunAsync("docker", "exec arkana-postgres pg_isready -U arkana");
                                    if (code == 0)
                                    {
                                        WizardRenderer.Success("PostgreSQL healthy");
                                        return;
                                    }
                                }
                                catch { /* not ready yet */ }

                                if (i == 10 || i == 20)
                                    WizardRenderer.Info($"Still waiting... ({i * 2}s)");
                                await Task.Delay(2000);
                            }
                            WizardRenderer.Warn("PostgreSQL health check timed out. Check container logs.");
                        });

                    // Show running containers
                    var (psOut, _) = await RunAsync("docker", "ps --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'");
                    foreach (var line in psOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        AnsiConsole.MarkupLine($"[grey]  {Markup.Escape(line.Trim())}[/]");

                    AnsiConsole.WriteLine();
                    return true;
                }
                catch (Exception ex)
                {
                    WizardRenderer.Error($"Docker failed: {ex.Message}");
                    return false;
                }
            });
    }

    public static async Task<bool> StopInfraAsync(SetupContext ctx)
    {
        var composeFile = Path.Combine(ctx.DeployDir, "docker-compose.yml");
        if (!File.Exists(composeFile)) return true;

        await AnsiConsole.Status()
            .StartAsync("Stopping containers...", async _ =>
            {
                await RunAsync("docker", $"compose -f \"{composeFile}\" down");
            });

        return true;
    }

    private static Task<(string Output, int ExitCode)> RunAsync(string command, string args)
    {
        var tcs = new TaskCompletionSource<(string, int)>();
        try
        {
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30000);
            tcs.TrySetResult((output, proc.ExitCode));
        }
        catch (Exception ex)
        {
            tcs.TrySetResult(($"Error: {ex.Message}", -1));
        }
        return tcs.Task;
    }
}
