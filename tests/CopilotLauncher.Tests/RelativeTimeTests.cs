using CopilotLauncher.Helpers;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class RelativeTimeTests
{
    private static readonly DateTime Now = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(60, "1m ago")]
    [InlineData(90, "1m ago")]
    [InlineData(59 * 60, "59m ago")]
    [InlineData(60 * 60, "1h ago")]
    [InlineData(23 * 3600, "23h ago")]
    [InlineData(24 * 3600, "1d ago")]
    [InlineData(6 * 86400, "6d ago")]
    public void Humanize_UsesIntervalsUnderAWeek(int secondsAgo, string expected)
    {
        Assert.Equal(expected, RelativeTime.Humanize(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void Humanize_FallsBackToAbsoluteDateBeyondAWeek()
    {
        var when = Now.AddDays(-30);
        Assert.Equal(when.ToLocalTime().ToString("yyyy-MM-dd"), RelativeTime.Humanize(when, Now));
    }

    [Fact]
    public void ToLocalDate_IsAlwaysAnAbsoluteDate()
    {
        var when = Now.AddMinutes(-5);
        Assert.Equal(when.ToLocalTime().ToString("yyyy-MM-dd"), RelativeTime.ToLocalDate(when));
    }
}
