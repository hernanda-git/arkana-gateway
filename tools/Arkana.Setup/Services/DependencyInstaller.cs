using Spectre.Console;
using Arkana.Setup.Models;
using Arkana.Setup.UI;

namespace Arkana.Setup.Services;

/// <summary>Batch installer — installs all missing prerequisites, one spinner per item.</summary>
public static class DependencyInstaller
{
    /// <summary>
    /// Given the current report, installs every missing dependency targeting the chosen platform.
    /// Returns an updated report after re-checking.
    /// </summary>
    public static async Task<PrerequisiteReport> InstallAndRecheckAsync(SetupContext ctx, PrerequisiteReport before)
    {
        var items = before.All.Where(r => !r.Found).ToList();
        if (items.Count == 0) return before;

        AnsiConsole.WriteLine();

        foreach (var item in items)
        {
            var command = GetInstallCommand(item.Label, ctx.Platform);
            if (command is null)
            {
                WizardRenderer.Warn($"{item.Label}: no auto-install available for {ctx.Platform}");
                continue;
            }

            var ok = await RunInstallAsync(command.Value, ctx);
            if (ok)
                WizardRenderer.Success($"{item.Label} installed");
            else
                WizardRenderer.Warn($"{item.Label} may need manual install");
        }

        AnsiConsole.WriteLine();

        // Re-check everything
        return await PrerequisiteChecker.CheckAllAsync(ctx);
    }

    private static async Task<bool> RunInstallAsync((string Shell, string Script) cmd, SetupContext ctx)
    {
        return await AnsiConsole.Status()
            .StartAsync($"Installing...", async _ =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(cmd.Shell, cmd.Script)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };

                    var proc = System.Diagnostics.Process.Start(psi)!;
                    var output = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync();

                    if (proc.ExitCode != 0)
                    {
                        var err = await proc.StandardError.ReadToEndAsync();
                        AnsiConsole.MarkupLine($"[grey]  ╰─ exit {proc.ExitCode}: {Markup.Escape(err.Trim()[..Math.Min(err.Trim().Length, 160)])}[/]");
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[grey]  ╰─ {Markup.Escape(ex.Message)}[/]");
                    return false;
                }
            });
    }

    private static (string Shell, string Script)? GetInstallCommand(string label, PlatformKind platform)
    {
        // .NET is special — we provide a link, not auto-install
        if (label.Contains(".NET")) return null;

        return (platform) switch
        {
            PlatformKind.Ubuntu or PlatformKind.Wsl => GetLinuxCommand(label),
            PlatformKind.MacOS => GetMacCommand(label),
            PlatformKind.Windows => GetWindowsCommand(label),
            _ => null
        };
    }

    private static (string Shell, string Script)? GetLinuxCommand(string label)
    {
        if (label.Contains("Docker Engine")) return ("bash", @"
sudo apt-get update -qq &&
sudo apt-get install -y -qq ca-certificates curl &&
sudo install -m 0755 -d /etc/apt/keyrings &&
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.asc -y 2>/dev/null &&
sudo chmod a+r /etc/apt/keyrings/docker.asc &&
echo 'deb [arch=amd64] https://download.docker.com/linux/ubuntu jammy stable' | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null &&
sudo apt-get update -qq &&
sudo apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-compose-plugin
");

        if (label.Contains("Docker Compose")) return ("bash", "sudo apt-get install -y -qq docker-compose-plugin 2>/dev/null || true");

        if (label.Contains("PostgreSQL")) return ("bash", @"
sudo sh -c 'echo ""deb http://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main"" > /etc/apt/sources.list.d/pgdg.list' &&
curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc | sudo gpg --dearmor -o /etc/apt/trusted.gpg.d/postgresql.gpg &&
sudo apt-get update -qq &&
sudo apt-get install -y -qq postgresql-16 postgresql-client-16 &&
sudo systemctl start postgresql 2>/dev/null || true
");

        if (label.Contains("Redis")) return ("bash", @"
sudo apt-get install -y -qq redis-server &&
sudo systemctl start redis-server 2>/dev/null || true
");

        return null;
    }

    private static (string Shell, string Script)? GetMacCommand(string label)
    {
        if (label.Contains("Docker Engine") || label.Contains("Docker Compose"))
            return ("bash", "brew install --cask docker");

        if (label.Contains("PostgreSQL"))
            return ("bash", "brew install postgresql@16 && brew services start postgresql@16");

        if (label.Contains("Redis"))
            return ("bash", "brew install redis && brew services start redis");

        return null;
    }

    private static (string Shell, string Script)? GetWindowsCommand(string label)
    {
        if (label.Contains("Docker Engine") || label.Contains("Docker Compose"))
            return ("powershell.exe", "-NoProfile -Command \"winget install Docker.DockerDesktop --silent\"");

        if (label.Contains("PostgreSQL"))
            return ("powershell.exe", "-NoProfile -Command \"winget install PostgreSQL.PostgreSQL.16 --silent\"");

        if (label.Contains("Redis"))
            return ("powershell.exe", "-NoProfile -Command \"docker run -d --name arkana-redis -p 6379:6379 redis:7-alpine 2>$null\"");

        return null;
    }
}
