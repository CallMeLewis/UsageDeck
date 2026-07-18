#if DEBUG
using UsageDeck.Core.Notifications;
using UsageDeck.Core.Providers;

namespace UsageDeck.App;

internal enum DebugNotificationScenario
{
    LimitWarning,
    LimitReached,
    LimitReset,
    CodexResetCredit,
    AuthenticationRequired,
    DataUnavailable,
    ConnectionRecovered,
    IncidentDetected,
    IncidentResolved,
}

internal static class DebugNotificationSamples
{
    public static UsageNotificationEvent Create(DebugNotificationScenario scenario) => scenario switch
    {
        DebugNotificationScenario.LimitWarning => new LimitThresholdCrossedNotification(
            ProviderId.Codex,
            "OpenAI Codex",
            "five-hour",
            "5-hour",
            UsedPercent: 84,
            RemainingThreshold: 20),
        DebugNotificationScenario.LimitReached => new LimitThresholdCrossedNotification(
            ProviderId.Codex,
            "OpenAI Codex",
            "five-hour",
            "5-hour",
            UsedPercent: 100,
            RemainingThreshold: 0),
        DebugNotificationScenario.LimitReset => new UsageWindowResetNotification(
            ProviderId.Codex,
            "OpenAI Codex",
            "five-hour",
            "5-hour",
            UsedPercent: 2),
        DebugNotificationScenario.CodexResetCredit => new CodexResetCreditGrantedNotification(
            ProviderId.Codex,
            "OpenAI Codex",
            GrantedCount: 1,
            AvailableCount: 2),
        DebugNotificationScenario.AuthenticationRequired => new ProviderAuthenticationRequiredNotification(
            ProviderId.Codex,
            "OpenAI Codex"),
        DebugNotificationScenario.DataUnavailable => new ProviderDataUnavailableNotification(
            ProviderId.Codex,
            "OpenAI Codex"),
        DebugNotificationScenario.ConnectionRecovered => new ProviderConnectionRecoveredNotification(
            ProviderId.Codex,
            "OpenAI Codex"),
        DebugNotificationScenario.IncidentDetected => new ProviderIncidentDetectedNotification(
            ProviderId.Codex,
            "OpenAI Codex",
            "Elevated errors are affecting some requests.",
            new Uri("https://status.openai.com/")),
        DebugNotificationScenario.IncidentResolved => new ProviderIncidentResolvedNotification(
            ProviderId.Codex,
            "OpenAI Codex"),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };
}
#endif
