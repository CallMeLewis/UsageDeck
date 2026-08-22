using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App;

internal static class UsageRefreshChangeDetector
{
    public static IReadOnlyCollection<ProviderId> AffectedProviders(
        AppSettings previous,
        AppSettings current) => AffectedProviders(previous, current, ProviderId.All);

    public static IReadOnlyCollection<ProviderId> AffectedProviders(
        AppSettings previous,
        AppSettings current,
        ProviderId selectedProvider)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        HashSet<ProviderId> affected = [];
        if (!previous.EnabledProviders.SequenceEqual(current.EnabledProviders))
        {
            affected.UnionWith(current.EnabledProviders);
        }

        if (previous.RefreshIntervalMinutes != current.RefreshIntervalMinutes)
        {
            affected.UnionWith(UsageRefreshScope.AutomaticProviders(current, selectedProvider));
        }

        if (previous.ZaiApiKeyStorage != current.ZaiApiKeyStorage
            || previous.ZaiRegion != current.ZaiRegion)
        {
            affected.Add(ProviderId.Zai);
        }

        if (previous.OpenCodeGoApiKeyStorage != current.OpenCodeGoApiKeyStorage
            || previous.OpenCodeGoUsageRange != current.OpenCodeGoUsageRange)
        {
            affected.Add(ProviderId.OpenCodeGo);
        }

        if (previous.TheClawBayUsageSource != current.TheClawBayUsageSource
            || previous.TheClawBayApiKeyStorage != current.TheClawBayApiKeyStorage)
        {
            affected.Add(ProviderId.TheClawBay);
        }

        affected.IntersectWith(current.EnabledProviders);
        return affected;
    }
}
