using UsageDeck.Core.Formatting;

namespace UsageDeck.App;

internal readonly record struct PresentationTimeCadence(
    TimeSpan TimerInterval,
    TimeDisplayPrecision Precision)
{
    public static PresentationTimeCadence FromRefreshInterval(int refreshIntervalMinutes) =>
        refreshIntervalMinutes == 1
            ? new(TimeSpan.FromSeconds(1), TimeDisplayPrecision.Seconds)
            : new(TimeSpan.FromSeconds(30), TimeDisplayPrecision.ThirtySeconds);
}
