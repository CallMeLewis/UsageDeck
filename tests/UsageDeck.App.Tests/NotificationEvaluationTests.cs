using UsageDeck.Core.Notifications;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App.Tests;

public sealed class NotificationEvaluationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

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

    private static ProviderSnapshot Usage(bool authenticationRequired = false) => new(
        ProviderId.Codex,
        "Codex",
        "Test source",
        Now,
        authenticationRequired ? UsageDataState.AuthenticationRequired : UsageDataState.Fresh,
        [new UsageWindow("session", "Session", 10)],
        errorCategory: authenticationRequired ? ProviderErrorCategory.AuthenticationRequired : null);

    private static ProviderServiceStatusSnapshot Status(bool hasProblems) => new(
        ProviderId.Codex,
        hasProblems ? ProviderServiceHealth.ProblemsReported : ProviderServiceHealth.Operational,
        hasProblems ? "Service problem" : "No problems reported.",
        Now);
}
