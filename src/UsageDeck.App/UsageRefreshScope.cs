using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App;

internal static class UsageRefreshScope
{
    public static IReadOnlyCollection<ProviderId> AutomaticProviders(
        AppSettings settings,
        ProviderId selectedProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (selectedProvider == ProviderId.All)
        {
            return settings.EnabledProviders.ToArray();
        }

        return settings.EnabledProviders.Contains(selectedProvider)
            ? [selectedProvider]
            : [];
    }
}
