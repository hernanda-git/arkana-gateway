using Spectre.Console;

namespace Arkana.Setup.UI;

/// <summary>Shared Spectre.Console helpers for a consistent wizard look.</summary>
public static class WizardRenderer
{
    public static void WelcomeBanner()
    {
        AnsiConsole.Write(new FigletText("ARKANA GATEWAY")
            .Color(Color.Blue));
        AnsiConsole.Write(new FigletText("SETUP")
            .Color(Color.Cyan));
        AnsiConsole.MarkupLine("[dim]ARKANA GATEWAY  ·  Cross-platform Setup Wizard  ·  v1.0[/]");
        AnsiConsole.MarkupLine("[dim]841 tests  ·  6 phases complete  ·  Maturity 7.5/10[/]");
        AnsiConsole.WriteLine();
    }

    public static Rule Section(string title)
    {
        return new Rule($"[bold yellow]{title}[/]")
        {
            Style = Style.Parse("yellow dim")
        };
    }

    public static void Status(string message)
    {
        AnsiConsole.MarkupLine($"[grey]  ╰─[/] {message}");
    }

    public static void Success(string message)
    {
        AnsiConsole.MarkupLine($"  [green]:check_mark_button:[/] {message}");
    }

    public static void Warn(string message)
    {
        AnsiConsole.MarkupLine($"  [yellow]:warning:[/] {message}");
    }

    public static void Error(string message)
    {
        AnsiConsole.MarkupLine($"  [red]:cross_mark:[/] {message}");
    }

    public static void Info(string message)
    {
        AnsiConsole.MarkupLine($"  [blue]:information:[/] {message}");
    }

    /// <summary>Wrap work with an animated spinner and status message.</summary>
    public static async Task<T> Spin<T>(string status, Func<ProgressContext, Task<T>> action)
    {
        return await AnsiConsole.Progress()
            .AutoClear(true)
            .Columns(new TaskDescriptionColumn(), new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask(status);
                var result = await action(ctx);
                task.Value = 100;
                return result;
            });
    }

    public static async Task Spin(string status, Func<ProgressContext, Task> action)
    {
        await AnsiConsole.Progress()
            .AutoClear(true)
            .Columns(new TaskDescriptionColumn(), new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask(status);
                await action(ctx);
                task.Value = 100;
            });
    }

    /// <summary>Display a results table.</summary>
    public static void ResultsTable(string title, IEnumerable<(string Key, string Value)> rows)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold green]{title}[/]")
            .AddColumn("Item")
            .AddColumn("Value");

        foreach (var (key, value) in rows)
            table.AddRow($"[bold]{key}[/]", value);

        AnsiConsole.Write(table);
    }

    /// <summary>Ask for a secret/API key with masked input.</summary>
    public static string PromptSecret(string prompt, bool required = true)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>($"[cyan]{prompt}[/]")
                .PromptStyle("grey")
                .Secret('•')
                .Validate(v => required && string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error("[red]Required[/]")
                    : ValidationResult.Success()));
    }

    /// <summary>Ask for text with default value.</summary>
    public static string PromptWithDefault(string prompt, string defaultValue)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>($"[cyan]{prompt}[/]")
                .PromptStyle("grey")
                .DefaultValue(defaultValue));
    }

    /// <summary>Ask for a yes/no confirmation.</summary>
    public static bool Confirm(string question, bool defaultValue = true)
    {
        return AnsiConsole.Confirm($"[cyan]{question}[/]", defaultValue);
    }
}
