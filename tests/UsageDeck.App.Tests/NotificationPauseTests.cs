using System.Globalization;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App.Tests;

public sealed class NotificationPauseTests
{
    private static readonly TimeZoneInfo TestTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        "UsageDeck.Tests.NotificationPause",
        TimeSpan.FromHours(2),
        "Test time",
        "Test time");

    [Fact]
    public void IsActiveOnlyBeforeDeadline()
    {
        DateTimeOffset deadline = new(2026, 8, 6, 13, 0, 0, TimeSpan.Zero);

        Assert.True(NotificationPause.IsActive(
            deadline,
            new DateTimeOffset(2026, 8, 6, 12, 59, 59, TimeSpan.Zero)));
        Assert.False(NotificationPause.IsActive(deadline, deadline));
        Assert.False(NotificationPause.IsActive(null, deadline));
    }

    [Fact]
    public void AllowsDeliveryRequiresNotificationsToBeEnabledAndUnpaused()
    {
        DateTimeOffset now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

        Assert.True(NotificationPause.AllowsDelivery(AppSettings.Default, now));
        Assert.False(NotificationPause.AllowsDelivery(
            AppSettings.Default with { AreNotificationsEnabled = false },
            now));
        Assert.False(NotificationPause.AllowsDelivery(
            AppSettings.Default with { NotificationsPausedUntilUtc = now.AddMinutes(1) },
            now));
        Assert.True(NotificationPause.AllowsDelivery(
            AppSettings.Default with { NotificationsPausedUntilUtc = now },
            now));
    }

    [Fact]
    public void GetTomorrowMorningUtcUsesTheLocalCalendarDate()
    {
        DateTimeOffset now = new(2026, 8, 6, 22, 30, 0, TimeSpan.Zero);

        DateTimeOffset result = NotificationPause.GetTomorrowMorningUtc(now, TestTimeZone);

        Assert.Equal(new DateTimeOffset(2026, 8, 8, 7, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void FormatStatusIdentifiesTomorrow()
    {
        DateTimeOffset now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset deadline = new(2026, 8, 7, 7, 0, 0, TimeSpan.Zero);

        string result = NotificationPause.FormatStatus(
            deadline,
            now,
            TestTimeZone,
            CultureInfo.GetCultureInfo("en-GB"));

        Assert.Equal("Notifications paused until tomorrow at 09:00", result);
    }
}
