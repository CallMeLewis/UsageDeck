using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App.Tests;

public sealed class UsageRefreshChangeDetectorTests
{
    [Fact]
    public void RefreshIntervalChangeAffectsOnlySelectedAutomaticScope()
    {
        AppSettings previous = AppSettings.Default with
        {
            EnabledProviders = [ProviderId.Codex, ProviderId.Claude, ProviderId.Amp],
        };
        AppSettings current = previous with { RefreshIntervalMinutes = 15 };

        IReadOnlyCollection<ProviderId> affected = UsageRefreshChangeDetector.AffectedProviders(
            previous,
            current,
            ProviderId.Claude);

        Assert.Equal([ProviderId.Claude], affected);
    }

    [Fact]
    public void ProviderAcquisitionSettingsRefreshOnlyTheirProvider()
    {
        AppSettings previous = AppSettings.Default with
        {
            EnabledProviders = [ProviderId.OpenCodeGo, ProviderId.Zai, ProviderId.TheClawBay],
        };
        AppSettings current = previous with
        {
            OpenCodeGoUsageRange = OpenCodeGoUsageRange.SevenDays,
            ZaiRegion = ZaiApiRegion.BigModelChina,
            TheClawBayUsageSource = TheClawBayUsageSource.ApiKey,
        };

        IReadOnlyCollection<ProviderId> affected = UsageRefreshChangeDetector.AffectedProviders(
            previous,
            current);

        Assert.Equal(
            [ProviderId.OpenCodeGo, ProviderId.TheClawBay, ProviderId.Zai],
            affected.OrderBy(providerId => providerId.Value));
    }

    [Fact]
    public void PresentationSettingsDoNotRefreshUsage()
    {
        AppSettings previous = AppSettings.Default;
        AppSettings current = previous with
        {
            Theme = AppThemePreference.Dark,
            UsageValueDisplay = UsageValueDisplayMode.Remaining,
        };

        IReadOnlyCollection<ProviderId> affected = UsageRefreshChangeDetector.AffectedProviders(
            previous,
            current);

        Assert.Empty(affected);
    }
}
