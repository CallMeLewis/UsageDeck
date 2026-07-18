namespace UsageDeck.Core.Notifications;

public sealed record NotificationEvaluationOptions
{
    public NotificationEvaluationOptions(
        IEnumerable<int>? remainingThresholds = null,
        bool notifyLimitResets = true,
        bool notifyCodexResetCredits = true,
        bool notifyProviderStatusChanges = true,
        bool notifyProviderConnectionChanges = true)
    {
        int[] thresholds = (remainingThresholds ?? [20, 5, 0])
            .Distinct()
            .OrderDescending()
            .ToArray();
        if (thresholds.Any(threshold => threshold is < 0 or > 100))
        {
            throw new ArgumentOutOfRangeException(
                nameof(remainingThresholds),
                "Remaining-capacity thresholds must be between 0 and 100.");
        }

        this.RemainingThresholds = thresholds;
        this.NotifyLimitResets = notifyLimitResets;
        this.NotifyCodexResetCredits = notifyCodexResetCredits;
        this.NotifyProviderStatusChanges = notifyProviderStatusChanges;
        this.NotifyProviderConnectionChanges = notifyProviderConnectionChanges;
    }

    public IReadOnlyList<int> RemainingThresholds { get; }

    public bool NotifyLimitResets { get; }

    public bool NotifyCodexResetCredits { get; }

    public bool NotifyProviderStatusChanges { get; }

    public bool NotifyProviderConnectionChanges { get; }
}
