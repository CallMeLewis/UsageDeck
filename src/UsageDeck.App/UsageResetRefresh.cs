using UsageDeck.Core.Providers;

namespace UsageDeck.App;

/// <summary>
/// Schedules a refresh shortly after a usage window resets, so an exhausted reading does not stay
/// on screen until the next automatic refresh.
/// </summary>
internal sealed class UsageResetRefresh
{
    // Providers can take a moment to report the new window after the reset time.
    internal static readonly TimeSpan ResetGracePeriod = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<ProviderId, DateTimeOffset> _dueAt = [];

    public void Record(ProviderSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DateTimeOffset? nextReset = snapshot.UsageWindows
            .Where(window => !window.IsUnlimited && window.ResetsAt > now)
            .Min(window => window.ResetsAt);
        lock (this._gate)
        {
            if (nextReset is DateTimeOffset resetsAt)
            {
                this._dueAt[snapshot.ProviderId] = resetsAt.Add(ResetGracePeriod);
            }
            else
            {
                this._dueAt.Remove(snapshot.ProviderId);
            }
        }
    }

    public IReadOnlyCollection<ProviderId> TakeDueProviders(IEnumerable<ProviderId> enabledProviders, DateTimeOffset now)
    {
        HashSet<ProviderId> enabled = enabledProviders.ToHashSet();
        lock (this._gate)
        {
            List<ProviderId> due = [];
            foreach ((ProviderId providerId, DateTimeOffset dueAt) in this._dueAt.ToArray())
            {
                if (!enabled.Contains(providerId))
                {
                    this._dueAt.Remove(providerId);
                }
                else if (now >= dueAt)
                {
                    due.Add(providerId);
                    this._dueAt.Remove(providerId);
                }
            }

            return due;
        }
    }
}
