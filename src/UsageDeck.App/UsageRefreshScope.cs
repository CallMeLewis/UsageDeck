using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App;

internal static class UsageRefreshScope
{
    public static IReadOnlyCollection<ProviderId> AutomaticProviders(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.EnabledProviders.ToArray();
    }
}
