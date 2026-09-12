using UsageDeck.Core.Notifications;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App.Tests;

public sealed class NotificationEvaluationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RejectedConnectionWarningDoesNotProduceRecovery()
    {
        NotificationEvaluator evaluator = new();
        AppSettings settings = AppSettings.Default;
        App.EvaluateUsageNotifications(evaluator, Usage(), settings, Now);
        IReadOnlyList<UsageNotificationEvent> warnings = App.EvaluateUsageNotifications(
            evaluator, Usage(authenticationRequired: true), settings, Now);
        Assert.Single(warnings);

        App.DeliverNotifications(evaluator, warnings, settings.UsageValueDisplay,
            _ => NotificationDeliveryResult.Failed("Windows rejected the notification."));

        Assert.Empty(App.EvaluateUsageNotifications(evaluator, Usage(), settings, Now));
    }

    [Fact]
    public void RejectedThresholdWarningRetriesWithCurrentUsage()
    {
        NotificationEvaluator evaluator = new();
        AppSettings settings = AppSettings.Default;
        App.EvaluateUsageNotifications(evaluator, Usage(usedPercent: 79), settings, Now);
        IReadOnlyList<UsageNotificationEvent> warnings = App.EvaluateUsageNotifications(
            evaluator, Usage(usedPercent: 81), settings, Now);
        Assert.Single(warnings);

        App.DeliverNotifications(evaluator, warnings, settings.UsageValueDisplay,
            _ => NotificationDeliveryResult.Failed("Windows rejected the notification."));

        LimitThresholdCrossedNotification retry = Assert.IsType<LimitThresholdCrossedNotification>(Assert.Single(
            App.EvaluateUsageNotifications(evaluator, Usage(usedPercent: 85), settings, Now)));
        Assert.Equal(85, retry.UsedPercent);
        App.DeliverNotifications(evaluator, [retry], settings.UsageValueDisplay,
            _ => NotificationDeliveryResult.Delivered);
        Assert.Empty(App.EvaluateUsageNotifications(evaluator, Usage(usedPercent: 86), settings, Now));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectedConnectionWarningRetriesAndOnlySuccessfulDeliveryAllowsRecovery(bool authenticationRequired)
    {
        NotificationEvaluator evaluator = new();
        AppSettings settings = AppSettings.Default;
        ProviderSnapshot failure = Usage().WithFailure(UsageDataState.Stale, "Usage could not be refreshed.",
            authenticationRequired ? ProviderErrorCategory.AuthenticationRequired : ProviderErrorCategory.Unavailable);
        App.EvaluateUsageNotifications(evaluator, Usage(), settings, Now);
        if (!authenticationRequired)
        {
            Assert.Empty(App.EvaluateUsageNotifications(evaluator, failure, settings, Now));
            Assert.Empty(App.EvaluateUsageNotifications(evaluator, failure, settings, Now));
        }

        IReadOnlyList<UsageNotificationEvent> warnings = App.EvaluateUsageNotifications(evaluator, failure, settings, Now);
        Assert.Single(warnings);
        App.DeliverNotifications(evaluator, warnings, settings.UsageValueDisplay,
            _ => NotificationDeliveryResult.Failed("Windows rejected the notification."));

        IReadOnlyList<UsageNotificationEvent> retry = App.EvaluateUsageNotifications(evaluator, failure, settings, Now);
        Assert.Single(retry);
        App.DeliverNotifications(evaluator, retry, settings.UsageValueDisplay,
            _ => NotificationDeliveryResult.Delivered);
        Assert.Empty(App.EvaluateUsageNotifications(evaluator, failure, settings, Now));
        Assert.IsType<ProviderConnectionRecoveredNotification>(Assert.Single(
            App.EvaluateUsageNotifications(evaluator, Usage(), settings, Now)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectedIncidentWarningOnlyAllowsResolutionAfterSuccessfulRetry(bool retrySuccessfully)
    {
        NotificationEvaluator evaluator = new();
        AppSettings settings = AppSettings.Default;
        App.EvaluateStatusNotifications(evaluator, Status(false), settings, Now);
        IReadOnlyList<UsageNotificationEvent> warnings = App.EvaluateStatusNotifications(evaluator, Status(true), settings, Now);
        Assert.Single(warnings);
        App.DeliverNotifications(evaluator, warnings, settings.UsageValueDisplay,
            _ => NotificationDeliveryResult.Failed("Windows rejected the notification."));

        if (retrySuccessfully)
        {
            IReadOnlyList<UsageNotificationEvent> retry = App.EvaluateStatusNotifications(evaluator, Status(true), settings, Now);
            Assert.IsType<ProviderIncidentDetectedNotification>(Assert.Single(retry));
            App.DeliverNotifications(evaluator, retry, settings.UsageValueDisplay,
                _ => NotificationDeliveryResult.Delivered);
            Assert.Empty(App.EvaluateStatusNotifications(evaluator, Status(true), settings, Now));
        }

        IReadOnlyList<UsageNotificationEvent> resolution = App.EvaluateStatusNotifications(evaluator, Status(false), settings, Now);
        if (retrySuccessfully)
        {
            Assert.IsType<ProviderIncidentResolvedNotification>(Assert.Single(resolution));
        }
        else
        {
            Assert.Empty(resolution);
        }
    }

    [Fact]
    public void PauseDiscardsRejectedWarningsWithoutReplayingThemOrSendingRecovery()
    {
        NotificationEvaluator evaluator = new();
        AppSettings settings = AppSettings.Default;
        AppSettings paused = settings with { NotificationsPausedUntilUtc = Now.AddHours(1) };
        App.EvaluateUsageNotifications(evaluator, Usage(usedPercent: 79), settings, Now);
        App.EvaluateStatusNotifications(evaluator, Status(false), settings, Now);
        IReadOnlyList<UsageNotificationEvent> thresholds = App.EvaluateUsageNotifications(evaluator, Usage(usedPercent: 81), settings, Now);
        IReadOnlyList<UsageNotificationEvent> incidents = App.EvaluateStatusNotifications(evaluator, Status(true), settings, Now);
        App.DeliverNotifications(evaluator, thresholds.Concat(incidents).ToArray(), settings.UsageValueDisplay,
            _ => NotificationDeliveryResult.Failed("Windows rejected the notification."));

        Assert.Empty(App.EvaluateUsageNotifications(evaluator, Usage(usedPercent: 82), paused, Now));
        Assert.Empty(App.EvaluateStatusNotifications(evaluator, Status(true), paused, Now));
        Assert.Empty(App.EvaluateUsageNotifications(evaluator, Usage(usedPercent: 83), settings, Now.AddHours(1)));
        Assert.Empty(App.EvaluateStatusNotifications(evaluator, Status(true), settings, Now.AddHours(1)));
        Assert.Empty(App.EvaluateStatusNotifications(evaluator, Status(false), settings, Now.AddHours(1)));
    }

    [Fact]
    public void AuthenticationFailureDuringPauseDoesNotProduceAnOrphanRecovery()
    {
        NotificationEvaluator evaluator = new();
        AppSettings paused = AppSettings.Default with { NotificationsPausedUntilUtc = Now.AddHours(1) };

        Assert.Empty(App.EvaluateUsageNotifications(evaluator, Usage(), paused, Now));
        Assert.Empty(App.EvaluateUsageNotifications(evaluator, Usage(authenticationRequired: true), paused, Now));
        Assert.Empty(App.EvaluateUsageNotifications(evaluator, Usage(), paused, Now.AddHours(1)));

        Assert.IsType<ProviderAuthenticationRequiredNotification>(Assert.Single(
            App.EvaluateUsageNotifications(evaluator, Usage(authenticationRequired: true), paused, Now.AddHours(1))));
        Assert.IsType<ProviderConnectionRecoveredNotification>(Assert.Single(
            App.EvaluateUsageNotifications(evaluator, Usage(), paused, Now.AddHours(1))));
    }

    [Fact]
    public void IncidentDuringPauseDoesNotProduceAnOrphanResolution()
    {
        NotificationEvaluator evaluator = new();
        AppSettings paused = AppSettings.Default with { NotificationsPausedUntilUtc = Now.AddHours(1) };

        Assert.Empty(App.EvaluateStatusNotifications(evaluator, Status(false), paused, Now));
        Assert.Empty(App.EvaluateStatusNotifications(evaluator, Status(true), paused, Now));
        Assert.Empty(App.EvaluateStatusNotifications(evaluator, Status(false), paused, Now.AddHours(1)));

        Assert.IsType<ProviderIncidentDetectedNotification>(Assert.Single(
            App.EvaluateStatusNotifications(evaluator, Status(true), paused, Now.AddHours(1))));
        Assert.IsType<ProviderIncidentResolvedNotification>(Assert.Single(
            App.EvaluateStatusNotifications(evaluator, Status(false), paused, Now.AddHours(1))));
    }

    [Fact]
    public void DisabledProviderIgnoresLateUsageAndDoesNotSeedNotificationHistory()
    {
        NotificationEvaluator evaluator = new();
        AppSettings disabled = AppSettings.Default with
        {
            EnabledProviders = [ProviderId.Claude],
            DefaultProvider = ProviderId.Claude,
        };
        App.EvaluateUsageNotifications(evaluator, Usage(), AppSettings.Default, Now);
        evaluator.RetainProviders(disabled.EnabledProviders);

        Assert.Empty(App.EvaluateUsageNotifications(evaluator, Usage(authenticationRequired: true), disabled, Now));
        Assert.IsType<ProviderAuthenticationRequiredNotification>(Assert.Single(
            App.EvaluateUsageNotifications(evaluator, Usage(authenticationRequired: true), AppSettings.Default, Now)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisabledStatusMonitoringOrProviderIgnoresLateStatus(bool disableProvider)
    {
        NotificationEvaluator evaluator = new();
        AppSettings disabled = disableProvider
            ? AppSettings.Default with { EnabledProviders = [ProviderId.Claude], DefaultProvider = ProviderId.Claude }
            : AppSettings.Default with { IsStatusMonitoringEnabled = false };

        Assert.Empty(App.EvaluateStatusNotifications(evaluator, Status(false), disabled, Now));
        // The ignored snapshot must not establish a baseline that produces a later incident alert.
        Assert.Empty(App.EvaluateStatusNotifications(evaluator, Status(true), AppSettings.Default, Now));
    }

    private static ProviderSnapshot Usage(bool authenticationRequired = false, double usedPercent = 10) => new(
        ProviderId.Codex,
        "Codex",
        "Test source",
        Now,
        authenticationRequired ? UsageDataState.AuthenticationRequired : UsageDataState.Fresh,
        [new UsageWindow("session", "Session", usedPercent)],
        errorCategory: authenticationRequired ? ProviderErrorCategory.AuthenticationRequired : null);

    private static ProviderServiceStatusSnapshot Status(bool hasProblems) => new(
        ProviderId.Codex,
        hasProblems ? ProviderServiceHealth.ProblemsReported : ProviderServiceHealth.Operational,
        hasProblems ? "Service problem" : "No problems reported.",
        Now);
}
