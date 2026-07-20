using UsageDeck.App;
using UsageDeck.Core.Formatting;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;
using UsageDeck.Infrastructure.Providers;

namespace UsageDeck.App.Tests;

public sealed class PresentationModelsTests
{
    [Fact]
    public void FirstRunSettingsOrderProvidersAndUseAllForMultipleSelections()
    {
        AppSettings result = FirstRunSettings.Create(
            AppSettings.Default,
            [ProviderId.Amp, ProviderId.Claude],
            AppThemePreference.Dark,
            notificationsEnabled: false);

        Assert.Equal([ProviderId.Claude, ProviderId.Amp], result.EnabledProviders);
        Assert.Equal(ProviderId.All, result.DefaultProvider);
        Assert.Equal(AppThemePreference.Dark, result.Theme);
        Assert.False(result.AreNotificationsEnabled);
    }

    [Fact]
    public void FirstRunSettingsUseTheOnlyProviderAsTheDefault()
    {
        AppSettings result = FirstRunSettings.Create(
            AppSettings.Default,
            [ProviderId.Kiro],
            AppThemePreference.System,
            notificationsEnabled: true);

        Assert.Equal([ProviderId.Kiro], result.EnabledProviders);
        Assert.Equal(ProviderId.Kiro, result.DefaultProvider);
    }

    [Fact]
    public void FirstRunSettingsRejectAnEmptyProviderSelection()
    {
        Assert.Throws<ArgumentException>(() => FirstRunSettings.Create(
            AppSettings.Default,
            [],
            AppThemePreference.System,
            notificationsEnabled: true));
    }

    [Fact]
    public void FirstRunDefaultsPreserveTheBuildUpdateChannel()
    {
        AppSettings current = AppSettings.Default with { UpdateChannel = AppUpdateChannel.Beta };

        AppSettings result = FirstRunSettings.CreateDefaults(current);

        Assert.Equal(AppSettings.Default.EnabledProviders, result.EnabledProviders);
        Assert.Equal(AppUpdateChannel.Beta, result.UpdateChannel);
    }

    [Fact]
    public void FirstRunProviderOptionPresentsDiscoveryWithoutAPath()
    {
        FirstRunProviderOption option = new(ProviderSettingsPresentation.All[ProviderId.Codex]);

        option.ApplyDiscovery(new ProviderDiscoveryResult(
            ProviderId.Codex,
            ProviderDiscoveryState.Detected,
            "Codex CLI was found on this PC."));

        Assert.Equal("Detected", option.DiscoveryText);
        Assert.Contains("Codex CLI was found", option.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderSettingsCoverEverySupportedProvider()
    {
        Assert.Equal(ProviderId.Supported.Count, ProviderSettingsPresentation.All.Count);

        foreach (ProviderId providerId in ProviderId.Supported)
        {
            Assert.True(ProviderSettingsPresentation.All.TryGetValue(
                providerId,
                out ProviderSettingsPresentation? provider));
            Assert.Equal(providerId, provider.Id);
            Assert.Equal(providerId.DisplayName, provider.DisplayName);
            Assert.Equal(providerId.Value, provider.ProviderKey);
            Assert.False(string.IsNullOrWhiteSpace(provider.Description));
            Assert.False(string.IsNullOrWhiteSpace(provider.UsageSource));
            Assert.False(string.IsNullOrWhiteSpace(provider.ConnectionLabel));
            Assert.False(string.IsNullOrWhiteSpace(provider.ConnectionValue));
            Assert.False(string.IsNullOrWhiteSpace(provider.AuthenticationSummary));
            Assert.False(string.IsNullOrWhiteSpace(provider.PrivacySummary));
        }
    }

    [Theory]
    [InlineData("codex", "codex")]
    [InlineData("claude", "claude")]
    [InlineData("antigravity", "agy")]
    [InlineData("copilot", "gh")]
    [InlineData("kiro", "kiro-cli")]
    [InlineData("amp", "amp")]
    [InlineData("opencode-go", "https://console.opencode.ai/api/v1/usage/export")]
    [InlineData("zai", "https://api.z.ai/api/monitor/usage/quota/limit")]
    public void ProviderSettingsNameTheExpectedConnection(string providerValue, string expectedConnection)
    {
        ProviderSettingsPresentation provider =
            ProviderSettingsPresentation.All[new ProviderId(providerValue)];

        Assert.Equal(expectedConnection, provider.ConnectionValue);
    }

    [Fact]
    public void AllTabUsesDedicatedNavigationIdentity()
    {
        ProviderTabViewModel model = new(ProviderId.All, ProviderId.All.DisplayName);

        Assert.True(model.IsAll);
        Assert.Equal("All providers", model.DisplayName);
        Assert.Equal("all", model.ProviderKey);
    }

    [Theory]
    [InlineData(480, 1, 480)]
    [InlineData(480, 1.25, 600)]
    [InlineData(401, 1.5, 602)]
    [InlineData(0, 2, 1)]
    public void WindowSizingConvertsLogicalUnitsToStablePhysicalPixels(
        double logicalUnits,
        double scale,
        int expectedPixels)
    {
        Assert.Equal(expectedPixels, WindowSizing.ToPixels(logicalUnits, scale));
    }

    [Theory]
    [InlineData(1, 1, TimeDisplayPrecision.Seconds)]
    [InlineData(5, 30, TimeDisplayPrecision.ThirtySeconds)]
    [InlineData(15, 30, TimeDisplayPrecision.ThirtySeconds)]
    [InlineData(30, 30, TimeDisplayPrecision.ThirtySeconds)]
    public void PresentationCadenceFollowsTheRefreshSetting(
        int refreshIntervalMinutes,
        int expectedTimerSeconds,
        TimeDisplayPrecision expectedPrecision)
    {
        PresentationTimeCadence cadence =
            PresentationTimeCadence.FromRefreshInterval(refreshIntervalMinutes);

        Assert.Equal(TimeSpan.FromSeconds(expectedTimerSeconds), cadence.TimerInterval);
        Assert.Equal(expectedPrecision, cadence.Precision);
    }

    [Fact]
    public void ApplySnapshotBuildsFreshAccessiblePresentationState()
    {
        DateTimeOffset now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        ProviderTabViewModel model = new(ProviderId.Codex, "OpenAI Codex");
        ProviderSnapshot snapshot = new(
            ProviderId.Codex,
            "OpenAI Codex",
            "Codex CLI",
            now,
            UsageDataState.Fresh,
            [new UsageWindow("session", "Current session", 42, now.AddHours(2))],
            new AccountIdentity(null, "pro-lite"),
            cliVersion: "0.144.5");

        model.ApplySnapshot(snapshot, now, TimeDisplayPrecision.Seconds);

        Assert.Equal("Updated just now · Codex CLI", model.UpdatedText);
        Assert.Equal("Updated just now", model.SummaryUpdatedText);
        Assert.Equal("Pro 5x", model.PlanText);
        Assert.Equal("CLI 0.144.5", model.CliVersionText);
        Assert.True(model.HasCliVersion);
        Assert.Equal("1 active limit", model.LimitsSummaryText);
        UsageWindowViewModel usage = Assert.Single(model.UsageWindows);
        Assert.Equal("42% used", usage.PercentText);
        Assert.Contains("Current session, 42% used", usage.AccessibleName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pro", "Pro 20x")]
    [InlineData("prolite", "Pro 5x")]
    [InlineData("pro_lite", "Pro 5x")]
    [InlineData("pro-lite", "Pro 5x")]
    [InlineData("pro lite", "Pro 5x")]
    public void ApplySnapshotFormatsCodexProTiers(string plan, string expected)
    {
        DateTimeOffset now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        ProviderTabViewModel model = new(ProviderId.Codex, "OpenAI Codex");
        ProviderSnapshot snapshot = new(
            ProviderId.Codex,
            "OpenAI Codex",
            "Codex CLI",
            now,
            UsageDataState.Fresh,
            [],
            new AccountIdentity(null, plan));

        model.ApplySnapshot(snapshot, now, TimeDisplayPrecision.Seconds);

        Assert.Equal(expected, model.PlanText);
    }

    [Fact]
    public void ApplySnapshotDoesNotRelabelOtherProvidersProPlans()
    {
        DateTimeOffset now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        ProviderTabViewModel model = new(ProviderId.Claude, "Claude");
        ProviderSnapshot snapshot = new(
            ProviderId.Claude,
            "Claude",
            "Claude Code",
            now,
            UsageDataState.Fresh,
            [],
            new AccountIdentity(null, "pro"));

        model.ApplySnapshot(snapshot, now, TimeDisplayPrecision.Seconds);

        Assert.Equal("Pro", model.PlanText);
    }

    [Fact]
    public void ApplySnapshotMarksAnInitiallyUnloadedProviderAsLoaded()
    {
        DateTimeOffset now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        ProviderTabViewModel model = new(ProviderId.Claude, "Claude");

        Assert.False(model.HasLoaded);

        model.ApplySnapshot(new ProviderSnapshot(
            ProviderId.Claude,
            "Claude",
            "Claude CLI",
            now,
            UsageDataState.Fresh), now, TimeDisplayPrecision.Seconds);

        Assert.True(model.HasLoaded);
        Assert.True(model.HasNoUsageData);
    }

    [Fact]
    public void ApplySnapshotCanHideCodexSparkUsageCards()
    {
        DateTimeOffset now = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        ProviderTabViewModel model = new(ProviderId.Codex, "OpenAI Codex");
        ProviderSnapshot snapshot = new(
            ProviderId.Codex,
            "OpenAI Codex",
            "Codex CLI",
            now,
            UsageDataState.Fresh,
            [
                new UsageWindow("session", "Session", 42),
                new UsageWindow("codex-spark", "GPT-5.3 Spark", 21),
                new UsageWindow("opaque-weekly-limit", "GPT-5.3 Spark Weekly", 18),
            ]);

        model.ApplySnapshot(snapshot, now, TimeDisplayPrecision.Seconds, showCodexSparkCard: false);

        UsageWindowViewModel remaining = Assert.Single(model.UsageWindows);
        Assert.Equal("Session", remaining.DisplayName);
        Assert.Equal("1 active limit", model.LimitsSummaryText);
    }

    [Fact]
    public void ApplySnapshotDoesNotHideSparkWindowsForOtherProviders()
    {
        DateTimeOffset now = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        ProviderTabViewModel model = new(ProviderId.Claude, "Claude");
        ProviderSnapshot snapshot = new(
            ProviderId.Claude,
            "Claude",
            "Claude CLI",
            now,
            UsageDataState.Fresh,
            [new UsageWindow("codex-spark", "Spark", 21)]);

        model.ApplySnapshot(snapshot, now, TimeDisplayPrecision.Seconds, showCodexSparkCard: false);

        Assert.Single(model.UsageWindows);
    }

    [Fact]
    public void OpenCodeGoEmptyStateExplainsApiAndLocalSources()
    {
        DateTimeOffset now = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        ProviderTabViewModel model = new(ProviderId.OpenCodeGo, "OpenCode Go");

        model.ApplySnapshot(new ProviderSnapshot(
            ProviderId.OpenCodeGo,
            "OpenCode Go",
            "Local OpenCode history",
            now,
            UsageDataState.Fresh), now, TimeDisplayPrecision.Seconds);

        Assert.True(model.HasNoUsageData);
        Assert.Equal("No OpenCode Go usage found", model.EmptyStateTitle);
        Assert.Contains("API billing range or local OpenCode history", model.EmptyStateMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplySnapshotPresentsProviderCreditBalances()
    {
        DateTimeOffset now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        ProviderTabViewModel model = new(ProviderId.Amp, "Amp");

        model.ApplySnapshot(new ProviderSnapshot(
            ProviderId.Amp,
            "Amp",
            "Amp CLI",
            now,
            UsageDataState.Fresh,
            credits: new CreditBalance("Individual: $12.50", HasCredits: true, IsUnlimited: false)),
            now,
            TimeDisplayPrecision.Seconds);

        Assert.True(model.HasCredits);
        Assert.Equal("Individual: $12.50", model.CreditsText);
        Assert.False(model.HasUsageWindows);
        Assert.False(model.HasNoUsageData);
    }

    [Fact]
    public void NeedsRefreshUsesTheConfiguredMaximumAge()
    {
        DateTimeOffset capturedAt = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        ProviderTabViewModel model = new(ProviderId.Claude, "Claude");

        Assert.True(model.NeedsRefresh(capturedAt, TimeSpan.FromMinutes(5)));

        model.ApplySnapshot(new ProviderSnapshot(
            ProviderId.Claude,
            "Claude",
            "Claude CLI",
            capturedAt,
            UsageDataState.Fresh), capturedAt, TimeDisplayPrecision.Seconds);

        Assert.False(model.NeedsRefresh(capturedAt.AddMinutes(4), TimeSpan.FromMinutes(5)));
        Assert.True(model.NeedsRefresh(capturedAt.AddMinutes(5), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void NeedsRefreshRejectsANegativeMaximumAge()
    {
        ProviderTabViewModel model = new(ProviderId.Codex, "OpenAI Codex");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            model.NeedsRefresh(DateTimeOffset.Now, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void UpdateTimeRecomputesStaleAgeAndResetCountdown()
    {
        DateTimeOffset capturedAt = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        ProviderTabViewModel model = new(ProviderId.Claude, "Claude");
        model.ApplySnapshot(new ProviderSnapshot(
            ProviderId.Claude,
            "Claude",
            "Claude CLI",
            capturedAt,
            UsageDataState.Stale,
            [new UsageWindow("session", "Current session", 10, capturedAt.AddMinutes(30))]),
            capturedAt,
            TimeDisplayPrecision.Seconds);

        model.UpdateTime(capturedAt.AddMinutes(10), TimeDisplayPrecision.Seconds);

        Assert.Equal("Showing saved data from 10m ago · Claude CLI", model.UpdatedText);
        Assert.Equal("Saved 10m ago", model.SummaryUpdatedText);
        Assert.Equal("Resets in 20m", Assert.Single(model.UsageWindows).ResetText);
    }

    [Fact]
    public void ResetTimeDisplaySwitchesBetweenCountdownAndExactLocalTime()
    {
        DateTimeOffset now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        UsageWindowViewModel model = new(
            new UsageWindow("session", "Current session", 10, now.AddMinutes(30)),
            now,
            TimeDisplayPrecision.Seconds);
        string exactResetText = model.ExactResetText;

        Assert.Equal("Resets in 30m", model.ResetText);
        Assert.Equal(exactResetText, model.AlternateResetText);

        model.UpdateTime(
            now.AddMinutes(10),
            TimeDisplayPrecision.Seconds,
            ResetTimeDisplayMode.ExactDateTime);

        Assert.Equal(exactResetText, model.ResetText);
        Assert.Equal("Resets in 20m", model.AlternateResetText);
        Assert.Contains(exactResetText, model.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageValueDisplaySwitchesBetweenUsedAndRemaining()
    {
        DateTimeOffset now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        UsageWindowViewModel model = new(
            new UsageWindow("session", "Current session", 42),
            now,
            TimeDisplayPrecision.Seconds);
        List<string?> changedProperties = [];
        model.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        Assert.Equal(42, model.DisplayPercent);
        Assert.Equal("42% used", model.PercentText);

        model.UpdateTime(
            now,
            TimeDisplayPrecision.Seconds,
            ResetTimeDisplayMode.Countdown,
            UsageValueDisplayMode.Remaining);

        Assert.Equal(58, model.DisplayPercent);
        Assert.Equal("58% remaining", model.PercentText);
        Assert.Contains("58% remaining", model.AccessibleName, StringComparison.Ordinal);
        Assert.Contains(nameof(UsageWindowViewModel.DisplayPercent), changedProperties);
        Assert.Contains(nameof(UsageWindowViewModel.PercentText), changedProperties);
    }

    [Theory]
    [InlineData(TimeDisplayPrecision.Seconds, 30, "Updated 30s ago · Claude CLI")]
    [InlineData(TimeDisplayPrecision.ThirtySeconds, 29, "Updated just now · Claude CLI")]
    [InlineData(TimeDisplayPrecision.ThirtySeconds, 30, "Updated 30s ago · Claude CLI")]
    [InlineData(TimeDisplayPrecision.ThirtySeconds, 60, "Updated 1m ago · Claude CLI")]
    public void UpdateTimeUsesTheConfiguredFreshnessPrecision(
        TimeDisplayPrecision precision,
        int elapsedSeconds,
        string expected)
    {
        DateTimeOffset capturedAt = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        ProviderTabViewModel model = new(ProviderId.Claude, "Claude");
        model.ApplySnapshot(new ProviderSnapshot(
            ProviderId.Claude,
            "Claude",
            "Claude CLI",
            capturedAt,
            UsageDataState.Fresh), capturedAt, precision);

        model.UpdateTime(capturedAt.AddSeconds(elapsedSeconds), precision);

        Assert.Equal(expected, model.UpdatedText);
    }

    [Fact]
    public void RefreshVisualStateInvalidatesBoundUsageColour()
    {
        UsageWindowViewModel model = new(
            new UsageWindow("session", "Current session", 75),
            DateTimeOffset.UtcNow,
            TimeDisplayPrecision.Seconds);
        List<string?> changedProperties = [];
        model.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        model.RefreshVisualState();

        Assert.Contains(nameof(UsageWindowViewModel.UsedPercent), changedProperties);
    }

    [Fact]
    public void UnlimitedWindowUsesTextInsteadOfAMisleadingMeter()
    {
        UsageWindowViewModel model = new(
            new UsageWindow("chat", "Chat", 0, isUnlimited: true),
            DateTimeOffset.UtcNow,
            TimeDisplayPrecision.Seconds);

        Assert.Equal("Unlimited", model.PercentText);
        Assert.Equal("No quota limit", model.ResetText);
        Assert.False(model.HasUsageMeter);
    }

    [Fact]
    public void ProviderFailureOffersRetryButLocalWarningDoesNot()
    {
        DateTimeOffset now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        ProviderTabViewModel model = new(ProviderId.Codex, "OpenAI Codex");
        ProviderSnapshot failure = new(
            ProviderId.Codex,
            "OpenAI Codex",
            "No source",
            DateTimeOffset.MinValue,
            UsageDataState.Unavailable,
            safeError: "OpenAI Codex usage could not be refreshed.");

        model.ApplySnapshot(failure, now, TimeDisplayPrecision.Seconds);

        Assert.True(model.CanRetry);
        Assert.True(model.HasStatusMessage);

        model.ShowWarning("The theme preference could not be saved.");

        Assert.False(model.CanRetry);
        Assert.True(model.HasStatusMessage);
    }

    [Fact]
    public void ServiceIncidentBuildsAnAccessibleWarningPresentation()
    {
        ProviderTabViewModel model = new(ProviderId.Codex, "OpenAI Codex");
        ProviderServiceStatusSnapshot snapshot = new(
            ProviderId.Codex,
            ProviderServiceHealth.ProblemsReported,
            "Increased server-overload errors",
            DateTimeOffset.UtcNow,
            new Uri("https://status.openai.com/"),
            new Uri("https://status.openai.com/incidents/example"));

        model.ApplyServiceStatus(snapshot, snapshot.OfficialStatusUri, monitoringEnabled: true);

        Assert.True(model.HasServiceProblem);
        Assert.Equal("Problems reported", model.ServiceStatusText);
        Assert.Equal("Increased server-overload errors", model.ServiceStatusDetail);
        Assert.Equal(snapshot.IncidentUri, model.OfficialStatusUri);
        Assert.Contains("OpenAI Codex, Problems reported", model.ServiceStatusAccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedRefreshDoesNotPresentTheLastKnownOperationalStateAsCurrent()
    {
        ProviderTabViewModel model = new(ProviderId.Claude, "Claude");
        ProviderServiceStatusSnapshot stale = new(
            ProviderId.Claude,
            ProviderServiceHealth.Operational,
            "No problems reported.",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new Uri("https://status.claude.com/"),
            IsStale: true,
            SafeError: "Claude status could not be refreshed.");

        model.ApplyServiceStatus(stale, stale.OfficialStatusUri, monitoringEnabled: true);

        Assert.False(model.HasServiceProblem);
        Assert.Equal("Couldn’t refresh", model.ServiceStatusText);
        Assert.Equal(ProviderStatusVisualLevel.Warning, model.ServiceStatusVisualLevel);
    }
}
