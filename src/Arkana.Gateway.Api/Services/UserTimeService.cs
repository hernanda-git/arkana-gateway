namespace Arkana.Gateway.Api.Services;

/// <summary>
/// Stores the user's IANA timezone ID (detected per-browser-session) and provides
/// UTC-to-local conversion helpers. Scoped per Blazor circuit.
///
/// Timezone detection chain (TrySet semantics):
///   browser IANA ID → invalid/unrecognised → config DefaultTimeZone → "UTC".
///
/// The public TimeZoneId setter is the integration point for JS interop (MainLayout.razor)
/// and any future per-user override; it clears the cached TimeZoneInfo so the new ID takes effect.
/// </summary>
public sealed class UserTimeService
{
    private string _timeZoneId;

    /// <summary>
    /// IANA timezone ID (e.g. "Asia/Jakarta").
    /// Setting this clears the cached TimeZoneInfo so the new ID takes effect.
    /// </summary>
    public string TimeZoneId
    {
        get => _timeZoneId;
        set
        {
            _timeZoneId = string.IsNullOrEmpty(value) ? "UTC" : value;
            _tzCache = null;
        }
    }

    /// <summary>
    /// Attempts to set the timezone from a browser-detected IANA ID.
    /// Returns true when the ID was accepted and is usable; false when it was
    /// invalid/unrecognised and the service fell back to the next chain link.
    /// Never throws — prerender / circuit-disconnect safety.
    /// </summary>
    public bool TrySetBrowserTimeZone(string? browserTimeZoneId)
    {
        if (string.IsNullOrWhiteSpace(browserTimeZoneId))
            return false;
        try
        {
            // Validate up front: if the IANA ID isn't on this host, don't switch to it.
            TimeZoneInfo.FindSystemTimeZoneById(browserTimeZoneId);
            TimeZoneId = browserTimeZoneId;
            return true;
        }
        catch
        {
            // Invalid on this host (e.g. minimal container without tzdata for that ID),
            // or unrecognised — fall through to the existing configured/UTC value.
            return false;
        }
    }

    private TimeZoneInfo? _tzCache;

    public UserTimeService(IConfiguration configuration)
    {
        var configuredTz = configuration["DefaultTimeZone"];
        _timeZoneId = string.IsNullOrEmpty(configuredTz) ? "UTC" : configuredTz;
    }

    private TimeZoneInfo GetTimeZone()
    {
        if (_tzCache is null)
        {
            try
            {
                _tzCache = TimeZoneInfo.FindSystemTimeZoneById(_timeZoneId);
            }
            catch
            {
                // If the IANA ID isn't recognised (e.g. on a minimal Docker image
                // without tzdata for that ID), fall back to the server's local timezone.
                _tzCache = TimeZoneInfo.Local;
            }
        }
        return _tzCache;
    }

    /// <summary>Converts a UTC DateTimeOffset to the user's local time.</summary>
    public DateTimeOffset ToLocal(DateTimeOffset utc)
        => TimeZoneInfo.ConvertTime(utc, GetTimeZone());

    /// <summary>Formats a UTC DateTimeOffset in the user's local time.</summary>
    public string Format(DateTimeOffset utc, string format = "yyyy-MM-dd HH:mm:ss")
        => ToLocal(utc).ToString(format);

    /// <summary>
    /// Compact date+time for dense tables (recent logs, provider last-seen, profile log table).
    /// Pattern: "MMM dd, HH:mm" — never time-only.
    /// </summary>
    public string FormatShort(DateTimeOffset utc)
        => ToLocal(utc).ToString("MMM dd, HH:mm");

    /// <summary>
    /// Returns the UTC offset as a signed-hours string like "UTC+07:00".
    /// </summary>
    public string UtcOffsetString
    {
        get
        {
            var offset = GetTimeZone().GetUtcOffset(DateTimeOffset.UtcNow);
#pragma warning disable CA1305 // IFormatProvider intentionally omitted for simple TimeSpan format
            return $"UTC{(offset >= TimeSpan.Zero ? "+" : "-")}{offset:hh\\:mm}";
#pragma warning restore CA1305
        }
    }
}
