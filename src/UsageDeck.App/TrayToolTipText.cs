using UsageDeck.Core.Formatting;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App;

/// <summary>
/// Summarises each enabled provider for the notification-area tooltip, so the usage that most
/// needs attention can be read without opening the window.
/// </summary>
internal static class TrayToolTipText
{
    public const string Title = "UsageDeck";

    // Windows truncates notification-area tooltips to 127 characters.
    public const int MaximumLength = 127;

    public static string Create(
        IEnumerable<ProviderId> enabledProviders,
        Func<ProviderId, ProviderSnapshot?> getSnapshot,
        DateTimeOffset now,
        UsageValueDisplayMode usageValueDisplay,
        bool showCodexSparkCard = true)
    {
        ArgumentNullException.ThrowIfNull(enabledProviders);
        ArgumentNullException.ThrowIfNull(getSnapshot);

        ProviderLine[] lines = enabledProviders
            .Select(providerId => CreateLine(
                providerId,
                getSnapshot(providerId),
                now,
                usageValueDisplay,
                showCodexSparkCard))
            .ToArray();
        string complete = string.Join('\n', lines.Select(line => line.Text).Prepend(Title));
        if (complete.Length <= MaximumLength)
        {
            return complete;
        }

        // When every provider cannot fit, keep the lines that most need attention.
        List<string> kept = [];
        int length = Title.Length;
        foreach (ProviderLine line in lines.OrderByDescending(line => line.Urgency))
        {
            int omitted = lines.Length - kept.Count - 1;
            int reserved = omitted > 0 ? $"\n+{omitted} more".Length : 0;
            if (length + 1 + line.Text.Length + reserved > MaximumLength)
            {
                break;
            }

            kept.Add(line.Text);
            length += 1 + line.Text.Length;
        }

        int remaining = lines.Length - kept.Count;
        IEnumerable<string> visible = kept.Prepend(Title);
        return string.Join('\n', remaining > 0 ? visible.Append($"+{remaining} more") : visible);
    }

    private static ProviderLine CreateLine(
        ProviderId providerId,
        ProviderSnapshot? snapshot,
        DateTimeOffset now,
        UsageValueDisplayMode usageValueDisplay,
        bool showCodexSparkCard)
    {
        string name = providerId.DisplayName;
        if (snapshot is null)
        {
            return new($"{name}: no data yet", Urgency.None);
        }

        if (snapshot.State == UsageDataState.AuthenticationRequired)
        {
            return new($"{name}: sign-in needed", Urgency.Problem);
        }

        UsageWindow? mostUsed = snapshot.UsageWindows
            .Where(window => window.UsageKnown && !window.IsUnlimited)
            .Where(window => showCodexSparkCard
                || providerId != ProviderId.Codex
                || !ProviderTabViewModel.IsCodexSparkWindow(window))
            .OrderByDescending(window => window.UsedPercent)
            .FirstOrDefault();
        if (mostUsed is null)
        {
            return snapshot.SafeError is null
                ? new($"{name}: no limits reported", Urgency.None)
                : new($"{name}: couldn’t refresh", Urgency.Problem);
        }

        double displayPercent = usageValueDisplay == UsageValueDisplayMode.Remaining
            ? 100 - mostUsed.UsedPercent
            : mostUsed.UsedPercent;
        string usage = UsageText.FormatPercentage(displayPercent)
            + (usageValueDisplay == UsageValueDisplayMode.Remaining ? " remaining" : " used");
        string text = mostUsed.ResetsAt is null
            ? $"{name}: {usage}"
            : $"{name}: {usage}, {LowercaseFirst(UsageText.FormatResetCountdown(mostUsed.ResetsAt, now, TimeDisplayPrecision.ThirtySeconds))}";
        if (snapshot.State == UsageDataState.Stale)
        {
            return new($"{text} (saved data)", Urgency.Problem);
        }

        // Matches the meter colours: amber at 50% or less remaining, red at 20% or less.
        return new(text, mostUsed.UsedPercent switch
        {
            >= 80 => Urgency.Low,
            >= 50 => Urgency.Watch,
            _ => Urgency.None,
        });
    }

    private static string LowercaseFirst(string text) =>
        text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    private enum Urgency
    {
        None,
        Watch,
        Problem,
        Low,
    }

    private readonly record struct ProviderLine(string Text, Urgency Urgency);
}
