using UsageDeck.Core.Notifications;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App;

internal sealed record NotificationMessage(
    string Title,
    string Body,
    ProviderId ProviderId,
    Uri? ActionUri = null,
    string? ActionLabel = null);

internal static class NotificationMessageFormatter
{
    public static NotificationMessage Format(
        UsageNotificationEvent notification,
        UsageValueDisplayMode displayMode) => notification switch
    {
        LimitThresholdCrossedNotification threshold => FormatThreshold(threshold, displayMode),
        UsageWindowResetNotification reset => new NotificationMessage(
            $"{reset.ProviderDisplayName} limit reset",
            $"The {reset.WindowDisplayName} allowance has reset. {FormatAvailable(reset.UsedPercent)}",
            reset.ProviderId),
        CodexResetCreditGrantedNotification credits => new NotificationMessage(
            credits.GrantedCount == 1
                ? "You received a Codex limit reset"
                : $"You received {credits.GrantedCount} Codex limit resets",
            credits.AvailableCount == 1
                ? "1 reset is now available."
                : $"{credits.AvailableCount} resets are now available.",
            credits.ProviderId),
        ProviderAuthenticationRequiredNotification authentication => new NotificationMessage(
            $"{authentication.ProviderDisplayName} sign-in required",
            "Open UsageDeck to restore usage monitoring.",
            authentication.ProviderId),
        ProviderDataUnavailableNotification unavailable => new NotificationMessage(
            $"{unavailable.ProviderDisplayName} usage is unavailable",
            "UsageDeck could not refresh this provider after several attempts.",
            unavailable.ProviderId),
        ProviderConnectionRecoveredNotification recovered => new NotificationMessage(
            $"{recovered.ProviderDisplayName} usage is available again",
            "UsageDeck can refresh this provider normally.",
            recovered.ProviderId),
        ProviderIncidentDetectedNotification incident => new NotificationMessage(
            $"{incident.ProviderDisplayName} reports service problems",
            incident.Summary,
            incident.ProviderId,
            incident.IncidentUri,
            incident.IncidentUri is null ? null : "View incident"),
        ProviderIncidentResolvedNotification resolved => new NotificationMessage(
            $"{resolved.ProviderDisplayName} is operational again",
            "The provider no longer reports an active service problem.",
            resolved.ProviderId),
        _ => throw new ArgumentOutOfRangeException(nameof(notification)),
    };

    private static NotificationMessage FormatThreshold(
        LimitThresholdCrossedNotification threshold,
        UsageValueDisplayMode displayMode)
    {
        if (threshold.RemainingThreshold == 0)
        {
            return new NotificationMessage(
                $"{threshold.ProviderDisplayName} limit reached",
                $"The {threshold.WindowDisplayName} allowance is exhausted.",
                threshold.ProviderId);
        }

        double percentage = displayMode == UsageValueDisplayMode.Remaining
            ? 100 - threshold.UsedPercent
            : threshold.UsedPercent;
        string qualifier = displayMode == UsageValueDisplayMode.Remaining ? "remaining" : "used";
        return new NotificationMessage(
            $"{threshold.ProviderDisplayName} limit warning",
            $"The {threshold.WindowDisplayName} allowance is {FormatPercentage(percentage)} {qualifier}.",
            threshold.ProviderId);
    }

    private static string FormatAvailable(double usedPercent) =>
        $"{FormatPercentage(100 - usedPercent)} is available.";

    private static string FormatPercentage(double value) =>
        $"{Math.Round(value, MidpointRounding.AwayFromZero):0}%";
}
