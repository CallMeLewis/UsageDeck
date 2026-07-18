using UsageDeck.Core.Providers;

namespace UsageDeck.Core.Notifications;

public abstract record UsageNotificationEvent(
    ProviderId ProviderId,
    string ProviderDisplayName);

public sealed record LimitThresholdCrossedNotification(
    ProviderId ProviderId,
    string ProviderDisplayName,
    string WindowId,
    string WindowDisplayName,
    double UsedPercent,
    int RemainingThreshold) : UsageNotificationEvent(ProviderId, ProviderDisplayName);

public sealed record UsageWindowResetNotification(
    ProviderId ProviderId,
    string ProviderDisplayName,
    string WindowId,
    string WindowDisplayName,
    double UsedPercent) : UsageNotificationEvent(ProviderId, ProviderDisplayName);

public sealed record CodexResetCreditGrantedNotification(
    ProviderId ProviderId,
    string ProviderDisplayName,
    long GrantedCount,
    long AvailableCount) : UsageNotificationEvent(ProviderId, ProviderDisplayName);

public sealed record ProviderAuthenticationRequiredNotification(
    ProviderId ProviderId,
    string ProviderDisplayName) : UsageNotificationEvent(ProviderId, ProviderDisplayName);

public sealed record ProviderDataUnavailableNotification(
    ProviderId ProviderId,
    string ProviderDisplayName) : UsageNotificationEvent(ProviderId, ProviderDisplayName);

public sealed record ProviderConnectionRecoveredNotification(
    ProviderId ProviderId,
    string ProviderDisplayName) : UsageNotificationEvent(ProviderId, ProviderDisplayName);

public sealed record ProviderIncidentDetectedNotification(
    ProviderId ProviderId,
    string ProviderDisplayName,
    string Summary,
    Uri? IncidentUri) : UsageNotificationEvent(ProviderId, ProviderDisplayName);

public sealed record ProviderIncidentResolvedNotification(
    ProviderId ProviderId,
    string ProviderDisplayName) : UsageNotificationEvent(ProviderId, ProviderDisplayName);
