using UsageDeck.Core.Notifications;
using UsageDeck.Core.Providers;

namespace UsageDeck.Core.Tests;

public sealed class NotificationEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecoveryWarnsAboutMostSevereNewCrossingInKnownSameCycle()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20, 10, 5, 0]);
        DateTimeOffset reset = Now.AddHours(5);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 79, resetsAt: reset), options);
        evaluator.EvaluateUsage(Snapshot(state: UsageDataState.Stale,
            errorCategory: ProviderErrorCategory.Transient), options);

        LimitThresholdCrossedNotification warning = Assert.IsType<LimitThresholdCrossedNotification>(Assert.Single(
            evaluator.EvaluateUsage(Snapshot(usedPercent: 96, resetsAt: reset, capturedAt: Now.AddMinutes(10)), options)));
        Assert.Equal(5, warning.RemainingThreshold);
        Assert.Empty(evaluator.EvaluateUsage(Snapshot(usedPercent: 97, resetsAt: reset, capturedAt: Now.AddMinutes(15)), options));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RecoveryStaysQuietWithoutAnUnexpiredMatchingReset(bool hasReset, bool changedCycle)
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20]);
        DateTimeOffset? reset = hasReset ? Now.AddMinutes(5) : null;
        evaluator.EvaluateUsage(Snapshot(usedPercent: 79, resetsAt: reset), options);
        evaluator.EvaluateUsage(Snapshot(state: UsageDataState.Stale, errorCategory: ProviderErrorCategory.Transient), options);
        DateTimeOffset? currentReset = changedCycle ? Now.AddHours(5) : reset;
        Assert.Empty(evaluator.EvaluateUsage(
            Snapshot(usedPercent: 96, resetsAt: currentReset, capturedAt: Now.AddMinutes(10)), options));
    }

    [Fact]
    public void RecoveryDoesNotReplayThresholdsSuppressedDuringTheFailure()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions enabled = new([20]);
        NotificationEvaluationOptions paused = new([]);
        DateTimeOffset reset = Now.AddHours(5);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 79, resetsAt: reset), enabled);
        evaluator.EvaluateUsage(Snapshot(state: UsageDataState.Stale, errorCategory: ProviderErrorCategory.Transient), enabled);
        evaluator.EvaluateUsage(Snapshot(state: UsageDataState.Stale, errorCategory: ProviderErrorCategory.Transient), paused);

        Assert.Empty(evaluator.EvaluateUsage(
            Snapshot(usedPercent: 96, resetsAt: reset, capturedAt: Now.AddMinutes(10)), enabled));
        Assert.Empty(evaluator.EvaluateUsage(
            Snapshot(usedPercent: 97, resetsAt: reset, capturedAt: Now.AddMinutes(15)), enabled));
    }

    [Fact]
    public void RecoveryDoesNotRepeatAnAlreadyDeliveredWarning()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20]);
        DateTimeOffset reset = Now.AddHours(5);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 79, resetsAt: reset), options);
        Assert.Single(evaluator.EvaluateUsage(Snapshot(usedPercent: 81, resetsAt: reset), options));
        evaluator.EvaluateUsage(Snapshot(state: UsageDataState.Stale, errorCategory: ProviderErrorCategory.Transient), options);
        Assert.Empty(evaluator.EvaluateUsage(
            Snapshot(usedPercent: 96, resetsAt: reset, capturedAt: Now.AddMinutes(10)), options));
    }

    [Fact]
    public void PauseBetweenFailedAndRecoveredReadingsPreventsCatchUpWarning()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20]);
        DateTimeOffset reset = Now.AddHours(5);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 79, resetsAt: reset), options);
        evaluator.EvaluateUsage(Snapshot(state: UsageDataState.Stale, errorCategory: ProviderErrorCategory.Transient), options);
        evaluator.UpdateRecoveryOptions(ProviderId.Codex, new NotificationEvaluationOptions([]));
        evaluator.UpdateRecoveryOptions(ProviderId.Codex, options);
        Assert.Empty(evaluator.EvaluateUsage(
            Snapshot(usedPercent: 96, resetsAt: reset, capturedAt: Now.AddMinutes(10)), options));
    }

    [Fact]
    public void RejectedThresholdIsSupersededByMoreSevereCrossing()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20, 10, 5, 0]);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 79), options);
        evaluator.ReportDeliveryFailure(Assert.Single(evaluator.EvaluateUsage(Snapshot(usedPercent: 81), options)));

        LimitThresholdCrossedNotification warning = Assert.IsType<LimitThresholdCrossedNotification>(Assert.Single(
            evaluator.EvaluateUsage(Snapshot(usedPercent: 96), options)));
        Assert.Equal(5, warning.RemainingThreshold);
        Assert.Empty(evaluator.EvaluateUsage(Snapshot(usedPercent: 97), options));
    }

    [Fact]
    public void RejectedThresholdDoesNotRetryAfterStaleReadingAndRecovery()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20]);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 79), options);
        evaluator.ReportDeliveryFailure(Assert.Single(evaluator.EvaluateUsage(Snapshot(usedPercent: 81), options)));

        Assert.Empty(evaluator.EvaluateUsage(Snapshot(usedPercent: 81, state: UsageDataState.Stale,
            errorCategory: ProviderErrorCategory.Transient), options));
        Assert.Empty(evaluator.EvaluateUsage(Snapshot(usedPercent: 82), options));
        Assert.Empty(evaluator.EvaluateUsage(Snapshot(usedPercent: 83), options));
    }

    [Fact]
    public void RejectedThresholdDoesNotRetryWhenUsageFallsAndCanWarnOnANewCrossing()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20]);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 79), options);
        evaluator.ReportDeliveryFailure(Assert.Single(evaluator.EvaluateUsage(Snapshot(usedPercent: 81), options)));

        Assert.Empty(evaluator.EvaluateUsage(Snapshot(usedPercent: 78), options));
        Assert.IsType<LimitThresholdCrossedNotification>(Assert.Single(
            evaluator.EvaluateUsage(Snapshot(usedPercent: 82), options)));
    }

    [Fact]
    public void RejectedWarningsAreClearedWhenProviderIsRemoved()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20]);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 79), options);
        evaluator.EvaluateStatus(Status(ProviderServiceHealth.Operational), options);
        evaluator.ReportDeliveryFailure(Assert.Single(evaluator.EvaluateUsage(Snapshot(usedPercent: 81), options)));
        evaluator.ReportDeliveryFailure(Assert.Single(evaluator.EvaluateStatus(Status(ProviderServiceHealth.ProblemsReported), options)));

        evaluator.RetainProviders([ProviderId.Claude]);

        Assert.Empty(evaluator.EvaluateUsage(Snapshot(usedPercent: 82), options));
        Assert.Empty(evaluator.EvaluateStatus(Status(ProviderServiceHealth.ProblemsReported), options));
        Assert.Empty(evaluator.EvaluateStatus(Status(ProviderServiceHealth.ProblemsReported), options));
        Assert.Empty(evaluator.EvaluateStatus(Status(ProviderServiceHealth.Operational), options));
    }

    [Fact]
    public void InitialUsageSnapshotWarmsEvaluatorWithoutNotifying()
    {
        NotificationEvaluator evaluator = new();

        IReadOnlyList<UsageNotificationEvent> notifications = evaluator.EvaluateUsage(
            Snapshot(usedPercent: 95),
            new NotificationEvaluationOptions());

        Assert.Empty(notifications);
    }

    [Fact]
    public void ThresholdCrossingUsesMostSevereCrossedThreshold()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20, 10, 5, 0]);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 79), options);

        UsageNotificationEvent notification = Assert.Single(
            evaluator.EvaluateUsage(Snapshot(usedPercent: 96, capturedAt: Now.AddMinutes(5)), options));

        LimitThresholdCrossedNotification threshold =
            Assert.IsType<LimitThresholdCrossedNotification>(notification);
        Assert.Equal(5, threshold.RemainingThreshold);
        Assert.Equal(96, threshold.UsedPercent);
    }

    [Fact]
    public void ThresholdDoesNotRepeatWithoutAnotherCrossing()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20]);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 79), options);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 81, capturedAt: Now.AddMinutes(5)), options);

        IReadOnlyList<UsageNotificationEvent> notifications = evaluator.EvaluateUsage(
            Snapshot(usedPercent: 85, capturedAt: Now.AddMinutes(10)),
            options);

        Assert.Empty(notifications);
    }

    [Fact]
    public void ThresholdDoesNotRepeatAfterUsageCorrectionWithinSameCycle()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20]);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 79), options);
        Assert.Single(evaluator.EvaluateUsage(
            Snapshot(usedPercent: 81, capturedAt: Now.AddMinutes(5)),
            options));
        evaluator.EvaluateUsage(Snapshot(usedPercent: 78, capturedAt: Now.AddMinutes(10)), options);

        IReadOnlyList<UsageNotificationEvent> notifications = evaluator.EvaluateUsage(
            Snapshot(usedPercent: 82, capturedAt: Now.AddMinutes(15)),
            options);

        Assert.Empty(notifications);
    }

    [Fact]
    public void IneligibleUsageWindowDoesNotProduceLimitNotifications()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20, 0]);
        evaluator.EvaluateUsage(
            Snapshot(usedPercent: 79, isLimitNotificationEligible: false),
            options);

        IReadOnlyList<UsageNotificationEvent> notifications = evaluator.EvaluateUsage(
            Snapshot(
                usedPercent: 100,
                capturedAt: Now.AddMinutes(5),
                isLimitNotificationEligible: false),
            options);

        Assert.Empty(notifications);
    }

    [Fact]
    public void IneligibleUsageWindowClearsPreviousThresholdState()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20]);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 81), options);
        evaluator.EvaluateUsage(
            Snapshot(
                usedPercent: 81,
                capturedAt: Now.AddMinutes(5),
                isLimitNotificationEligible: false),
            options);
        evaluator.EvaluateUsage(
            Snapshot(usedPercent: 79, capturedAt: Now.AddMinutes(10)),
            options);

        IReadOnlyList<UsageNotificationEvent> notifications = evaluator.EvaluateUsage(
            Snapshot(usedPercent: 81, capturedAt: Now.AddMinutes(15)),
            options);

        Assert.IsType<LimitThresholdCrossedNotification>(Assert.Single(notifications));
    }

    [Fact]
    public void StaleUsageDoesNotCrossThreshold()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20]);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 79), options);

        IReadOnlyList<UsageNotificationEvent> notifications = evaluator.EvaluateUsage(
            Snapshot(
                usedPercent: 90,
                capturedAt: Now.AddMinutes(5),
                state: UsageDataState.Stale,
                errorCategory: ProviderErrorCategory.Transient),
            options);

        Assert.Empty(notifications);
    }

    [Fact]
    public void KnownUsageWindowResetNotifiesInsteadOfCrossingThreshold()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20]);
        evaluator.EvaluateUsage(
            Snapshot(usedPercent: 92, resetsAt: Now.AddMinutes(5)),
            options);

        UsageNotificationEvent notification = Assert.Single(evaluator.EvaluateUsage(
            Snapshot(
                usedPercent: 4,
                capturedAt: Now.AddMinutes(10),
                resetsAt: Now.AddHours(5)),
            options));

        Assert.IsType<UsageWindowResetNotification>(notification);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecoveryOnlyRearmsThresholdsWhenTheUsageCycleChanged(bool cycleChanged)
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new([20]);
        DateTimeOffset originalReset = Now.AddMinutes(10);
        evaluator.EvaluateUsage(Snapshot(usedPercent: 79, resetsAt: originalReset), options);
        Assert.Single(evaluator.EvaluateUsage(
            Snapshot(usedPercent: 81, capturedAt: Now.AddMinutes(5), resetsAt: originalReset), options));
        evaluator.EvaluateUsage(Snapshot(
            usedPercent: 81,
            capturedAt: Now.AddMinutes(10),
            state: UsageDataState.Stale,
            errorCategory: ProviderErrorCategory.Transient), options);
        DateTimeOffset nextReset = cycleChanged ? Now.AddHours(5) : originalReset;

        Assert.Empty(evaluator.EvaluateUsage(
            Snapshot(usedPercent: 10, capturedAt: Now.AddMinutes(15), resetsAt: nextReset), options));
        IReadOnlyList<UsageNotificationEvent> notifications = evaluator.EvaluateUsage(
            Snapshot(usedPercent: 81, capturedAt: Now.AddMinutes(20), resetsAt: nextReset), options);

        if (cycleChanged)
        {
            Assert.IsType<LimitThresholdCrossedNotification>(Assert.Single(notifications));
        }
        else
        {
            Assert.Empty(notifications);
        }
    }

    [Fact]
    public void IncreasedCodexResetCreditCountNotifies()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new();
        evaluator.EvaluateUsage(Snapshot(resetCreditCount: 1), options);

        UsageNotificationEvent notification = Assert.Single(evaluator.EvaluateUsage(
            Snapshot(capturedAt: Now.AddMinutes(5), resetCreditCount: 3),
            options));

        CodexResetCreditGrantedNotification credits =
            Assert.IsType<CodexResetCreditGrantedNotification>(notification);
        Assert.Equal(2, credits.GrantedCount);
        Assert.Equal(3, credits.AvailableCount);
    }

    [Fact]
    public void AuthenticationFailureAndRecoveryEachNotifyOnce()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new();
        evaluator.EvaluateUsage(Snapshot(), options);

        ProviderSnapshot failure = Snapshot(
            capturedAt: Now.AddMinutes(5),
            state: UsageDataState.Stale,
            errorCategory: ProviderErrorCategory.AuthenticationRequired);
        Assert.IsType<ProviderAuthenticationRequiredNotification>(
            Assert.Single(evaluator.EvaluateUsage(failure, options)));
        Assert.Empty(evaluator.EvaluateUsage(
            failure.WithFailure(
                UsageDataState.Stale,
                "Sign-in is still required.",
                ProviderErrorCategory.AuthenticationRequired),
            options));

        Assert.IsType<ProviderConnectionRecoveredNotification>(Assert.Single(
            evaluator.EvaluateUsage(Snapshot(capturedAt: Now.AddMinutes(10)), options)));
    }

    [Fact]
    public void SuppressedConnectionFailureDoesNotProduceRecoveryNotification()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions disabled = new(notifyProviderConnectionChanges: false);
        evaluator.EvaluateUsage(Snapshot(), disabled);
        evaluator.EvaluateUsage(
            Snapshot(
                capturedAt: Now.AddMinutes(5),
                state: UsageDataState.Stale,
                errorCategory: ProviderErrorCategory.AuthenticationRequired),
            disabled);

        IReadOnlyList<UsageNotificationEvent> notifications = evaluator.EvaluateUsage(
            Snapshot(capturedAt: Now.AddMinutes(10)),
            new NotificationEvaluationOptions());

        Assert.Empty(notifications);
    }

    [Fact]
    public void InitialAuthenticationFailureAndRecoveryEachNotifyOnce()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new();
        ProviderSnapshot failure = Snapshot(
            state: UsageDataState.AuthenticationRequired,
            errorCategory: ProviderErrorCategory.AuthenticationRequired);

        Assert.IsType<ProviderAuthenticationRequiredNotification>(
            Assert.Single(evaluator.EvaluateUsage(failure, options)));
        Assert.Empty(evaluator.EvaluateUsage(
            failure.WithFailure(
                UsageDataState.AuthenticationRequired,
                "Sign-in is still required.",
                ProviderErrorCategory.AuthenticationRequired),
            options));
        Assert.IsType<ProviderConnectionRecoveredNotification>(Assert.Single(
            evaluator.EvaluateUsage(
                Snapshot(capturedAt: Now.AddMinutes(5)),
                options)));
    }

    [Fact]
    public void UnavailableRequiresThreeFailuresBeforeNotifying()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new();
        evaluator.EvaluateUsage(Snapshot(), options);

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            Assert.Empty(evaluator.EvaluateUsage(
                Snapshot(
                    capturedAt: Now.AddMinutes(attempt * 5),
                    state: UsageDataState.Stale,
                    errorCategory: ProviderErrorCategory.Transient),
                options));
        }

        Assert.IsType<ProviderDataUnavailableNotification>(Assert.Single(
            evaluator.EvaluateUsage(
                Snapshot(
                    capturedAt: Now.AddMinutes(15),
                    state: UsageDataState.Stale,
                    errorCategory: ProviderErrorCategory.Unavailable),
                options)));
    }

    [Fact]
    public void ProviderIncidentStartAndResolutionNotify()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new();
        evaluator.EvaluateStatus(Status(ProviderServiceHealth.Operational), options);

        Assert.IsType<ProviderIncidentDetectedNotification>(Assert.Single(
            evaluator.EvaluateStatus(Status(ProviderServiceHealth.ProblemsReported), options)));
        Assert.Empty(evaluator.EvaluateStatus(Status(ProviderServiceHealth.ProblemsReported), options));
        Assert.IsType<ProviderIncidentResolvedNotification>(Assert.Single(
            evaluator.EvaluateStatus(Status(ProviderServiceHealth.Operational), options)));
    }

    [Fact]
    public void ResetStatusClearsIncidentHistoryBeforeMonitoringRestarts()
    {
        NotificationEvaluator evaluator = new();
        NotificationEvaluationOptions options = new();
        evaluator.EvaluateStatus(Status(ProviderServiceHealth.Operational), options);
        Assert.Single(evaluator.EvaluateStatus(Status(ProviderServiceHealth.ProblemsReported), options));

        evaluator.ResetStatus();

        Assert.Empty(evaluator.EvaluateStatus(Status(ProviderServiceHealth.ProblemsReported), options));
        Assert.Empty(evaluator.EvaluateStatus(Status(ProviderServiceHealth.Operational), options));
    }

    private static ProviderSnapshot Snapshot(
        double usedPercent = 40,
        DateTimeOffset? capturedAt = null,
        DateTimeOffset? resetsAt = null,
        UsageDataState state = UsageDataState.Fresh,
        ProviderErrorCategory? errorCategory = null,
        long? resetCreditCount = null,
        bool isLimitNotificationEligible = true) => new(
        ProviderId.Codex,
        "Codex",
        "Test source",
        capturedAt ?? Now,
        state,
        [new UsageWindow(
            "five-hour",
            "5-hour",
            usedPercent,
            resetsAt,
            isLimitNotificationEligible: isLimitNotificationEligible)],
        resetCredits: resetCreditCount is long count ? new RateLimitResetCredits(count) : null,
        safeError: errorCategory is null ? null : "Usage could not be refreshed.",
        errorCategory: errorCategory);

    private static ProviderServiceStatusSnapshot Status(ProviderServiceHealth health) => new(
        ProviderId.Codex,
        health,
        health == ProviderServiceHealth.ProblemsReported
            ? "Elevated error rates."
            : "No problems reported.",
        Now,
        new Uri("https://status.example.com/"),
        health == ProviderServiceHealth.ProblemsReported
            ? new Uri("https://status.example.com/incidents/1")
            : null);
}
