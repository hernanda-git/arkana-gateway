using Spectre.Console;
using Arkana.Setup.Models;

namespace Arkana.Setup.Services;

/// <summary>Detects host OS, distro, and recommends best setup mode.</summary>
public static class PlatformDetector
{
    public static PlatformKind Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            // Check if running inside WSL (detected via /proc/version)
            var isWsl = false;
            try
            {
                var procVersion = File.ReadAllText("/proc/version");
                isWsl = procVersion.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                        procVersion.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
            }
            catch { /* not WSL */ }

            if (isWsl)
            {
                AnsiConsole.MarkupLine("[grey]  ├─ Detected: [bold]WSL[/] (Windows Subsystem for Linux)[/]");
                return PlatformKind.Wsl;
            }

            AnsiConsole.MarkupLine("[grey]  ├─ Detected: [bold]Windows[/][/]");
            return PlatformKind.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            var distro = DetectLinuxDistro();
            AnsiConsole.MarkupLine($"[grey]  ├─ Detected: [bold]{distro}[/][/]");

            if (distro.Contains("ubuntu", StringComparison.OrdinalIgnoreCase) ||
                distro.Contains("debian", StringComparison.OrdinalIgnoreCase))
                return PlatformKind.Ubuntu;

            // Generic Linux — recommend Docker
            AnsiConsole.MarkupLine("[yellow]  ╰─ Non-Ubuntu Linux detected. Docker mode recommended.[/]");
            return PlatformKind.Docker;
        }

        if (OperatingSystem.IsMacOS())
        {
            AnsiConsole.MarkupLine("[grey]  ├─ Detected: [bold]macOS[/][/]");
            return PlatformKind.MacOS;
        }

        // Fallback: Docker
        AnsiConsole.MarkupLine("[yellow]  ├─ Unknown OS. Falling back to Docker mode.[/]");
        return PlatformKind.Docker;
    }

    private static string DetectLinuxDistro()
    {
        try
        {
            if (File.Exists("/etc/os-release"))
            {
                var lines = File.ReadAllLines("/etc/os-release");
                var nameLine = lines.FirstOrDefault(l => l.StartsWith("ID=", StringComparison.OrdinalIgnoreCase));
                if (nameLine != null)
                    return nameLine.Split('=', 2)[1].Trim('"');
            }
        }
        catch { /* ignore */ }
        return "linux";
    }

    /// <summary>Let the user override the detected platform.</summary>
    public static PlatformKind PromptOverride(PlatformKind detected)
    {
        var choices = new Dictionary<string, PlatformKind>
        {
            ["Ubuntu / Debian (native)"] = PlatformKind.Ubuntu,
            ["Windows (native)"] = PlatformKind.Windows,
            ["macOS (native)"] = PlatformKind.MacOS,
            ["Docker (any OS — recommended)"] = PlatformKind.Docker
        };

        // Add WSL option only if detected
        if (detected == PlatformKind.Wsl)
            choices["WSL (Ubuntu on Windows)"] = PlatformKind.Wsl;

        var label = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Setup mode:[/]")
                .PageSize(6)
                .HighlightStyle("green")
                .MoreChoicesText("[grey](scroll for more)[/]")
                .AddChoices(choices.Keys));

        return choices[label];
    }
}
