using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App;

internal static class SettingsMutations
{
    public static AppSettings SetProviderEnabled(
        AppSettings settings,
        ProviderId providerId,
        bool isEnabled)
    {
        ArgumentNullException.ThrowIfNull(settings);

        HashSet<ProviderId> enabledProviders = settings.EnabledProviders.ToHashSet();
        if (isEnabled)
        {
            enabledProviders.Add(providerId);
        }
        else
        {
            enabledProviders.Remove(providerId);
        }

        ProviderId[] enabled = ProviderId.Available.Where(enabledProviders.Contains).ToArray();
        if (enabled.Length == 0)
        {
            return settings;
        }

        ProviderId defaultProvider = (settings.DefaultProvider == ProviderId.All && settings.IsAllTabEnabled)
            || enabled.Contains(settings.DefaultProvider)
                ? settings.DefaultProvider
                : enabled[0];

        return settings with
        {
            EnabledProviders = enabled,
            DefaultProvider = defaultProvider,
        };
    }
}
