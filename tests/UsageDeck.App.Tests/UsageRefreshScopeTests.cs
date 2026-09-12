using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App.Tests;

public sealed class UsageRefreshScopeTests
{
    [Fact]
    public void AutomaticRefreshIncludesEveryEnabledProvider()
    {
        AppSettings settings = AppSettings.Default with
        {
            EnabledProviders = [ProviderId.Codex, ProviderId.Claude, ProviderId.Amp],
        };

        IReadOnlyCollection<ProviderId> providers = UsageRefreshScope.AutomaticProviders(
            settings);

        Assert.Equal(settings.EnabledProviders, providers);
    }

    [Fact]
    public void IndividualDefaultTabWithAllTabHiddenStillMonitorsEveryEnabledProvider()
    {
        AppSettings settings = AppSettings.Default with
        {
            DefaultProvider = ProviderId.Claude,
            IsAllTabEnabled = false,
            EnabledProviders = [ProviderId.Codex, ProviderId.Claude, ProviderId.Amp],
        };

        IReadOnlyCollection<ProviderId> providers = UsageRefreshScope.AutomaticProviders(
            settings);

        Assert.Equal(settings.EnabledProviders, providers);
    }
}
