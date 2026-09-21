using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Arkana.Gateway.Api.Services;

namespace Arkana.Gateway.Api.Tests.Services;

public class UserTimeServiceTests
{
    public UserTimeServiceTests()
    {
        // Format assertions below expect English month names ("Aug 26").
        // Pin invariant culture so results don't depend on the host locale (id-ID etc.).
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }

    private static IConfiguration MakeConfiguration(string? defaultTimeZone)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DefaultTimeZone"] = defaultTimeZone
        };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void Constructor_WithConfiguredTimeZone_StoresIt()
    {
        var svc = new UserTimeService(MakeConfiguration("Asia/Jakarta"));
        svc.TimeZoneId.Should().Be("Asia/Jakarta");
    }

    [Fact]
    public void Constructor_WithEmptyConfig_FallsBackToUtc()
    {
        var svc = new UserTimeService(MakeConfiguration(null));
        svc.TimeZoneId.Should().Be("UTC");
    }

    [Fact]
    public void Constructor_WithWhitespaceConfig_StoresAsIs()
    {
        var svc = new UserTimeService(MakeConfiguration("   "));
        svc.TimeZoneId.Should().Be("   ");
    }

    [Fact]
    public void TrySetBrowserTimeZone_ValidId_SwitchesAndReturnsTrue()
    {
        var svc = new UserTimeService(MakeConfiguration("UTC"));
        svc.TrySetBrowserTimeZone("Asia/Jakarta").Should().BeTrue();
        svc.TimeZoneId.Should().Be("Asia/Jakarta");
    }

    [Fact]
    public void TrySetBrowserTimeZone_Whitespace_ReturnsFalseAndKeepsCurrent()
    {
        var svc = new UserTimeService(MakeConfiguration("Asia/Jakarta"));
        var before = svc.TimeZoneId;
        svc.TrySetBrowserTimeZone("   ").Should().BeFalse();
        svc.TimeZoneId.Should().Be(before);
    }

    [Fact]
    public void TrySetBrowserTimeZone_Null_ReturnsFalseAndKeepsCurrent()
    {
        var svc = new UserTimeService(MakeConfiguration("Asia/Jakarta"));
        var before = svc.TimeZoneId;
        svc.TrySetBrowserTimeZone(null).Should().BeFalse();
        svc.TimeZoneId.Should().Be(before);
    }

    [Fact]
    public void TrySetBrowserTimeZone_InvalidAfterValid_KeepsPreviousValidId()
    {
        var svc = new UserTimeService(MakeConfiguration("UTC"));
        svc.TrySetBrowserTimeZone("Asia/Jakarta").Should().BeTrue();
        svc.TrySetBrowserTimeZone("Not/ARealZone").Should().BeFalse();
        svc.TimeZoneId.Should().Be("Asia/Jakarta");
    }

    [Fact]
    public void ToLocal_ConversionsAreDstSafe_ForAmericaNewYork()
    {
        var svc = new UserTimeService(MakeConfiguration("America/New_York"));
        var jan = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        svc.ToLocal(jan).Should().Be(new DateTimeOffset(2026, 1, 15, 7, 0, 0, TimeSpan.FromHours(-5)));
        var jul = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        svc.ToLocal(jul).Should().Be(new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.FromHours(-4)));
    }

    [Fact]
    public void Format_DefaultPattern_IsFullDateTime()
    {
        var svc = new UserTimeService(MakeConfiguration("Asia/Jakarta"));
        var utc = new DateTimeOffset(2026, 8, 26, 10, 30, 0, TimeSpan.Zero);
        var result = svc.Format(utc);
        result.Should().Contain("2026-08-26");
        result.Should().Contain("17:30");
    }

    [Fact]
    public void Format_CustomPattern_PassesThrough()
    {
        var svc = new UserTimeService(MakeConfiguration("Asia/Jakarta"));
        var utc = new DateTimeOffset(2026, 8, 26, 10, 30, 0, TimeSpan.Zero);
        svc.Format(utc, "MMM dd, yyyy · HH:mm").Should().Be("Aug 26, 2026 · 17:30");
    }

    [Fact]
    public void FormatShort_ContainsDateAndTime_NeverTimeOnly()
    {
        var svc = new UserTimeService(MakeConfiguration("Asia/Jakarta"));
        var utc = new DateTimeOffset(2026, 8, 26, 10, 30, 0, TimeSpan.Zero);
        var shortFmt = svc.FormatShort(utc);
        shortFmt.Should().Contain("Aug 26");
        shortFmt.Should().Contain("17:30");
        // Time-only would be 8 chars (HH:mm:ss); bertanggal has date too.
        shortFmt.Length.Should().BeGreaterThan(8);
    }

    [Fact]
    public void UtcOffsetString_ForJakarta_ReturnsUtcPlusSeven()
    {
        var svc = new UserTimeService(MakeConfiguration("Asia/Jakarta"));
        svc.UtcOffsetString.Should().Be("UTC+07:00");
    }

    [Fact]
    public void UtcOffsetString_ForAmericaNewYork_IsAFormattedOffset()
    {
        var svc = new UserTimeService(MakeConfiguration("America/New_York"));
        var offset = svc.UtcOffsetString;
        offset.Should().StartWith("UTC");
        offset.Should().HaveLength(9);
        offset.Should().Contain(":");
    }

    [Fact]
    public void SettingTimeZoneId_ClearsCache_SoSubsequentReadsUseNewId()
    {
        var svc = new UserTimeService(MakeConfiguration("UTC"));
        svc.TimeZoneId = "Asia/Jakarta";
        svc.UtcOffsetString.Should().Be("UTC+07:00");
    }

    [Fact]
    public void Format_WorksForUtcTimestamp_ProducesDatedOutput()
    {
        var svc = new UserTimeService(MakeConfiguration("Asia/Jakarta"));
        var formatted = svc.Format(DateTimeOffset.UtcNow);
        formatted.Should().Contain("-");
        formatted.Should().Contain(":");
        formatted.Should().HaveLength(19);
    }
}
