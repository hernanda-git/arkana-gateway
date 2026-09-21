using Arkana.Gateway.Api.Endpoints;

namespace Arkana.Gateway.Api.Components.Pages;

/// <summary>
/// One quota-window progress card (5-hour or weekly) rendered on the profile
/// page, with a live "resets in HH:MM" countdown driven by the page's ticking
/// clock. Pure render component; holds no state of its own.
/// </summary>
public sealed class UsageCard : Microsoft.AspNetCore.Components.ComponentBase
{
    /// <summary>The window snapshot to render.</summary>
    [Microsoft.AspNetCore.Components.Parameter] public ProfileEndpoints.UsageWindowDto Window { get; set; } = null!;

    /// <summary>Ticking UTC clock from the owning page (drives the countdown).</summary>
    [Microsoft.AspNetCore.Components.Parameter] public DateTimeOffset Now { get; set; }

    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        var pct = Window.UsedPercent;
        var level = pct > 90 ? "crit" : pct >= 70 ? "warn" : "ok";
        var width = Math.Clamp(pct, 0, 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        var pctText = pct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "usage-card");

        // Top row: title + account code.
        builder.OpenElement(2, "div");
        builder.AddAttribute(3, "class", "usage-card-top");
        builder.OpenElement(4, "span");
        builder.AddAttribute(5, "class", "usage-card-title");
        builder.AddContent(6, Window.Window == "5h" ? "5-hour window" : "Weekly window");
        builder.CloseElement(); // span title
        builder.OpenElement(7, "span");
        builder.AddAttribute(8, "class", "usage-card-account");
        builder.AddContent(9, Window.AccountCode);
        builder.CloseElement(); // span account
        builder.CloseElement(); // div top

        // Big percentage.
        builder.OpenElement(10, "strong");
        builder.AddAttribute(11, "class", "usage-percent");
        builder.AddContent(12, $"{pctText}% used");
        builder.CloseElement();

        // Meter fill.
        builder.OpenElement(13, "div");
        builder.AddAttribute(14, "class", "usage-meter");
        builder.OpenElement(15, "div");
        builder.AddAttribute(16, "class", $"usage-meter-fill {level}");
        builder.AddAttribute(17, "style", $"width:{width}%");
        builder.CloseElement(); // fill
        builder.CloseElement(); // meter

        // Meta row: countdown + staleness flag.
        builder.OpenElement(18, "div");
        builder.AddAttribute(19, "class", "usage-meta");
        builder.OpenElement(20, "span");
        if (Window.ResetsAtUtc is { } reset)
            builder.AddContent(21, $"resets {Format(reset, Now)}");
        else
            builder.AddContent(21, "reset time unknown");
        builder.CloseElement(); // span countdown

        if (Window.IsStale)
        {
            builder.OpenElement(22, "span");
            builder.AddAttribute(23, "class", "stale-flag");
            builder.AddContent(24, "stale — awaiting next request");
            builder.CloseElement();
        }
        builder.CloseElement(); // div meta

        builder.CloseElement(); // div card
    }

    private static string Format(DateTimeOffset resetsAtUtc, DateTimeOffset now)
    {
        var remaining = resetsAtUtc - now;
        if (remaining <= TimeSpan.Zero)
            return "now";
        if (remaining.TotalDays >= 2)
            return $"in {(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1)
            return $"in {remaining.Hours}h {remaining.Minutes:D2}m";
        return $"in {remaining.Minutes}m {(int)remaining.Seconds:D2}s";
    }
}
