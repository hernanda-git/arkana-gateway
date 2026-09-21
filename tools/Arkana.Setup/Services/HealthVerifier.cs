using System.Net.Http.Json;
using Spectre.Console;
using Arkana.Setup.Models;
using Arkana.Setup.UI;

namespace Arkana.Setup.Services;

/// <summary>Builds the solution and verifies the gateway responds correctly.</summary>
public static class HealthVerifier
{
    public static async Task<bool> VerifyAsync(SetupContext ctx)
    {
        AnsiConsole.Write(WizardRenderer.Section("Build & Verify"));
        AnsiConsole.WriteLine();

        // Step 1: Build
        if (!await BuildSolutionAsync(ctx))
            return false;

        // Step 2: Run tests
        if (!await RunTestsAsync(ctx))
        {
            if (!WizardRenderer.Confirm("Some tests failed. Continue anyway?"))
                return false;
        }

        // Step 3: Generate and persist API key
        ctx.GeneratedApiKey = $"arkana-{Guid.NewGuid():N}";

        // Save API key to .api-key file for later reference
        var apiKeyPath = Path.Combine(ctx.SolutionDir, ".api-key");
        await File.WriteAllTextAsync(apiKeyPath, ctx.GeneratedApiKey + Environment.NewLine);
        WizardRenderer.Success($"API key saved to [grey]{apiKeyPath}[/]");
        AnsiConsole.MarkupLine($"  [bold yellow]Your API Key:[/] [bold]{ctx.GeneratedApiKey}[/]");

        // Step 4: HTTP health check
        if (!await HttpHealthCheckAsync(ctx))
        {
            WizardRenderer.Warn("Gateway not responding yet. It may still be starting.");
            WizardRenderer.Info($"Run: [bold]dotnet run --project {Path.Combine(ctx.SolutionDir, "src", "Arkana.Gateway.Api")}[/]");
        }

        return true;
    }

    private static async Task<bool> BuildSolutionAsync(SetupContext ctx)
    {
        return await AnsiConsole.Status()
            .StartAsync("Building solution...", async _ =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("dotnet", "build")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = ctx.SolutionDir
                    };

                    var proc = System.Diagnostics.Process.Start(psi)!;
                    var output = await proc.StandardOutput.ReadToEndAsync();
                    var error = await proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync();

                    if (proc.ExitCode != 0)
                    {
                        // Extract error count
                        var errCount = 0;
                        foreach (var line in output.Split('\n'))
                        {
                            if (line.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                                line.Contains(".cs", StringComparison.OrdinalIgnoreCase))
                                errCount++;
                        }

                        WizardRenderer.Error($"Build failed with {errCount} error(s)");
                        WizardRenderer.Info(error.Trim()[..Math.Min(error.Trim().Length, 300)]);
                        return false;
                    }

                    // Extract warning count
                    var warnCount = 0;
                    foreach (var line in output.Split('\n'))
                    {
                        if (line.Contains("warning", StringComparison.OrdinalIgnoreCase) &&
                            line.Contains(".cs", StringComparison.OrdinalIgnoreCase))
                            warnCount++;
                    }

                    WizardRenderer.Success($"Build succeeded [grey]({warnCount} warnings)[/]");
                    return true;
                }
                catch (Exception ex)
                {
                    WizardRenderer.Error($"Build error: {ex.Message}");
                    return false;
                }
            });
    }

    private static async Task<bool> RunTestsAsync(SetupContext ctx)
    {
        return await AnsiConsole.Status()
            .StartAsync("Running tests...", async _ =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("dotnet", "test")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = ctx.SolutionDir
                    };

                    var proc = System.Diagnostics.Process.Start(psi)!;
                    var output = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync();

                    // Parse test results
                    var passMatch = System.Text.RegularExpressions.Regex.Match(output, @"Passed!\s+.*Passed:\s+(\d+)");
                    var failMatch = System.Text.RegularExpressions.Regex.Match(output, @"Failed:\s+(\d+)");
                    var totalMatch = System.Text.RegularExpressions.Regex.Match(output, @"Total:\s+(\d+)");

                    var passed = passMatch.Success ? passMatch.Groups[1].Value : "?";
                    var failed = failMatch.Success ? failMatch.Groups[1].Value : "0";
                    var total = totalMatch.Success ? totalMatch.Groups[1].Value : "?";

                    if (proc.ExitCode == 0)
                    {
                        WizardRenderer.Success($"{total} tests passed [grey](0 failed)[/]");
                        return true;
                    }

                    WizardRenderer.Warn($"{failed} test(s) failed (out of {total})");
                    return false;
                }
                catch (Exception ex)
                {
                    WizardRenderer.Warn($"Tests could not run: {ex.Message}");
                    return false;
                }
            });
    }

    private static async Task<bool> HttpHealthCheckAsync(SetupContext ctx)
    {
        return await AnsiConsole.Status()
            .StartAsync("Verifying gateway health...", async _ =>
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var response = await http.GetAsync($"{ctx.GatewayUrl}/");
                    ctx.HealthCheckPassed = response.IsSuccessStatusCode;
                    WizardRenderer.Success($"Gateway responded: [grey]{(int)response.StatusCode} {response.ReasonPhrase}[/]");
                    return true;
                }
                catch
                {
                    WizardRenderer.Warn("Gateway not reachable (will work after launch)");
                    return false;
                }
            });
    }
}
