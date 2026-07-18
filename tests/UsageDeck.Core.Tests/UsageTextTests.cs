using System.Globalization;
using UsageDeck.Core.Formatting;

namespace UsageDeck.Core.Tests;

public sealed class UsageTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-10, "just now")]
    [InlineData(3, "just now")]
    [InlineData(30, "30s ago")]
    [InlineData(90, "1m ago")]
    [InlineData(300, "5m ago")]
    [InlineData(7200, "2h ago")]
    [InlineData(183600, "2d ago")]
    public void FormatAgeUsesStableUnits(int elapsedSeconds, string expected)
    {
        string result = UsageText.FormatAge(
            Now,
            Now.AddSeconds(elapsedSeconds),
            TimeDisplayPrecision.Seconds);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(29, "just now")]
    [InlineData(30, "30s ago")]
    [InlineData(59, "30s ago")]
    [InlineData(60, "1m ago")]
    [InlineData(90, "1m ago")]
    public void FormatAgeUsesThirtySecondPrecision(int elapsedSeconds, string expected)
    {
        string result = UsageText.FormatAge(
            Now,
            Now.AddSeconds(elapsedSeconds),
            TimeDisplayPrecision.ThirtySeconds);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(30, "Resets in 30s")]
    [InlineData(90, "Resets in 2m")]
    [InlineData(8040, "Resets in 2h 14m")]
    [InlineData(172800, "Resets in 2d")]
    [InlineData(183600, "Resets in 2d 3h")]
    public void FormatResetCountdownUsesStableUnits(int seconds, string expected)
    {
        string result = UsageText.FormatResetCountdown(
            Now.AddSeconds(seconds),
            Now,
            TimeDisplayPrecision.Seconds);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(20, "Resets in 30s")]
    [InlineData(30, "Resets in 30s")]
    [InlineData(31, "Resets in 1m")]
    [InlineData(90, "Resets in 2m")]
    public void FormatResetCountdownUsesThirtySecondPrecision(int seconds, string expected)
    {
        string result = UsageText.FormatResetCountdown(
            Now.AddSeconds(seconds),
            Now,
            TimeDisplayPrecision.ThirtySeconds);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatResetCountdownHandlesMissingAndPastValues()
    {
        Assert.Equal(
            "Reset time unavailable",
            UsageText.FormatResetCountdown(null, Now, TimeDisplayPrecision.Seconds));
        Assert.Equal(
            "Reset due",
            UsageText.FormatResetCountdown(Now.AddSeconds(-1), Now, TimeDisplayPrecision.Seconds));
    }

    [Fact]
    public void FormatExactResetUsesRequestedTimeZone()
    {
        TimeZoneInfo utc = TimeZoneInfo.Utc;

        string result = UsageText.FormatExactReset(
            new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero),
            utc,
            CultureInfo.GetCultureInfo("en-GB"));

        Assert.Equal("Resets Tue 21 Jul, 08:00", result);
    }

    [Fact]
    public void FormatExactExpiryUsesRequestedTimeZone()
    {
        string result = UsageText.FormatExactExpiry(
            new DateTimeOffset(2026, 8, 12, 17, 36, 57, TimeSpan.Zero),
            TimeZoneInfo.Utc,
            CultureInfo.GetCultureInfo("en-GB"));

        Assert.Equal("Expires Wed 12 Aug, 17:36", result);
        Assert.Equal("Expiry unavailable", UsageText.FormatExactExpiry(null));
    }
}
