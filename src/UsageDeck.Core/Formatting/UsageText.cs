using System.Globalization;

namespace UsageDeck.Core.Formatting;

public enum TimeDisplayPrecision
{
    Seconds,
    ThirtySeconds,
}

public static class UsageText
{
    public static string FormatPercentage(double usedPercent, CultureInfo? culture = null)
    {
        if (!double.IsFinite(usedPercent))
        {
            return "—";
        }

        return Math.Clamp(usedPercent, 0, 100).ToString("0", culture ?? CultureInfo.CurrentCulture) + "%";
    }

    public static string FormatAge(
        DateTimeOffset capturedAt,
        DateTimeOffset now,
        TimeDisplayPrecision precision)
    {
        ValidatePrecision(precision);

        TimeSpan elapsed = now - capturedAt;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            if (elapsed < TimeSpan.FromSeconds(5)
                || precision == TimeDisplayPrecision.ThirtySeconds
                    && elapsed < TimeSpan.FromSeconds(30))
            {
                return "just now";
            }

            if (precision == TimeDisplayPrecision.ThirtySeconds)
            {
                return "30s ago";
            }

            return $"{Math.Max(1, (int)elapsed.TotalSeconds)}s ago";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)}m ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalHours)}h ago";
        }

        return $"{Math.Max(1, (int)elapsed.TotalDays)}d ago";
    }

    public static string FormatResetCountdown(
        DateTimeOffset? resetsAt,
        DateTimeOffset now,
        TimeDisplayPrecision precision)
    {
        ValidatePrecision(precision);

        if (resetsAt is null)
        {
            return "Reset time unavailable";
        }

        TimeSpan remaining = resetsAt.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "Reset due";
        }

        if (remaining < TimeSpan.FromMinutes(1))
        {
            if (precision == TimeDisplayPrecision.ThirtySeconds)
            {
                return remaining <= TimeSpan.FromSeconds(30)
                    ? "Resets in 30s"
                    : "Resets in 1m";
            }

            int seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
            return $"Resets in {seconds}s";
        }

        if (remaining < TimeSpan.FromHours(1))
        {
            int minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            return $"Resets in {minutes}m";
        }

        if (remaining < TimeSpan.FromDays(2))
        {
            int hours = (int)remaining.TotalHours;
            int minutes = remaining.Minutes;
            return minutes == 0
                ? $"Resets in {hours}h"
                : $"Resets in {hours}h {minutes}m";
        }

        int days = (int)remaining.TotalDays;
        int trailingHours = remaining.Hours;
        return trailingHours == 0
            ? $"Resets in {days}d"
            : $"Resets in {days}d {trailingHours}h";
    }

    public static string FormatExactReset(
        DateTimeOffset? resetsAt,
        TimeZoneInfo? timeZone = null,
        CultureInfo? culture = null)
    {
        if (resetsAt is null)
        {
            return "Reset time unavailable";
        }

        TimeZoneInfo targetZone = timeZone ?? TimeZoneInfo.Local;
        DateTimeOffset local = TimeZoneInfo.ConvertTime(resetsAt.Value, targetZone);
        return "Resets " + local.ToString("ddd d MMM, HH:mm", culture ?? CultureInfo.CurrentCulture);
    }

    public static string FormatExactExpiry(
        DateTimeOffset? expiresAt,
        TimeZoneInfo? timeZone = null,
        CultureInfo? culture = null)
    {
        if (expiresAt is null)
        {
            return "Expiry unavailable";
        }

        TimeZoneInfo targetZone = timeZone ?? TimeZoneInfo.Local;
        DateTimeOffset local = TimeZoneInfo.ConvertTime(expiresAt.Value, targetZone);
        return "Expires " + local.ToString("ddd d MMM, HH:mm", culture ?? CultureInfo.CurrentCulture);
    }

    private static void ValidatePrecision(TimeDisplayPrecision precision)
    {
        if (precision is not TimeDisplayPrecision.Seconds and not TimeDisplayPrecision.ThirtySeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(precision), "The time display precision is not supported.");
        }
    }
}
