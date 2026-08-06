using System.Globalization;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App;

internal static class NotificationPause
{
    private const int TomorrowMorningHour = 9;

    public static bool IsActive(DateTimeOffset? pausedUntilUtc, DateTimeOffset nowUtc) =>
        pausedUntilUtc is DateTimeOffset deadline && deadline > nowUtc.ToUniversalTime();

    public static bool AllowsDelivery(AppSettings settings, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.AreNotificationsEnabled
            && !IsActive(settings.NotificationsPausedUntilUtc, nowUtc);
    }

    public static DateTimeOffset GetTomorrowMorningUtc(
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        DateTime localDate = TimeZoneInfo.ConvertTime(now, timeZone).Date;
        DateTime localTomorrowMorning = DateTime.SpecifyKind(
            localDate.AddDays(1).AddHours(TomorrowMorningHour),
            DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localTomorrowMorning, timeZone);
    }

    public static string FormatStatus(
        DateTimeOffset pausedUntilUtc,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        CultureInfo displayCulture = culture ?? CultureInfo.CurrentCulture;
        DateTime localDeadline = TimeZoneInfo.ConvertTime(pausedUntilUtc, timeZone).DateTime;
        DateTime localToday = TimeZoneInfo.ConvertTime(now, timeZone).Date;
        if (localDeadline.Date == localToday)
        {
            return $"Notifications paused until {localDeadline.ToString("t", displayCulture)}";
        }

        if (localDeadline.Date == localToday.AddDays(1))
        {
            return $"Notifications paused until tomorrow at {localDeadline.ToString("t", displayCulture)}";
        }

        return $"Notifications paused until {localDeadline.ToString("ddd d MMM, HH:mm", displayCulture)}";
    }
}
