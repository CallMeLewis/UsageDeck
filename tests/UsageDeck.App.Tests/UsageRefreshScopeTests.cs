using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App.Tests;

public sealed class UsageRefreshScopeTests
{
    [Fact]
    public void AllTabRefreshesEveryEnabledProvider()
    {
        AppSettings settings = AppSettings.Default with
        {
            EnabledProviders = [ProviderId.Codex, ProviderId.Claude, ProviderId.Amp],
        };

        IReadOnlyCollection<ProviderId> providers = UsageRefreshScope.AutomaticProviders(
            settings,
            ProviderId.All);

        Assert.Equal(settings.EnabledProviders, providers);
    }

    [Fact]
    public void IndividualTabRefreshesOnlyTheSelectedProvider()
    {
        AppSettings settings = AppSettings.Default with
        {
            EnabledProviders = [ProviderId.Codex, ProviderId.Claude, ProviderId.Amp],
        };

        IReadOnlyCollection<ProviderId> providers = UsageRefreshScope.AutomaticProviders(
            settings,
            ProviderId.Claude);

        Assert.Equal([ProviderId.Claude], providers);
    }
}
