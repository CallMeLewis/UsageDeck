using UsageDeck.Core.Providers;

namespace UsageDeck.App;

internal sealed class UsageFailureConfirmation
{
    private static readonly TimeSpan ConfirmationInterval = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly Dictionary<ProviderId, FailureState> _failures = [];

    public void Record(ProviderSnapshot snapshot, DateTimeOffset now)
    {
        lock (this._gate)
        {
            if (snapshot.State == UsageDataState.Fresh)
            {
                this._failures.Remove(snapshot.ProviderId);
                return;
            }

            int previousFailures = this._failures.GetValueOrDefault(snapshot.ProviderId)?.Count ?? 0;
            int failures = snapshot.ErrorCategory == ProviderErrorCategory.AuthenticationRequired
                ? 0 : Math.Min(3, previousFailures + 1);
            bool confirm = failures < 3 && snapshot.ErrorCategory is
                ProviderErrorCategory.Transient or ProviderErrorCategory.Unavailable or ProviderErrorCategory.InvalidResponse;
            DateTimeOffset due = now.Add(ConfirmationInterval);
            if (snapshot.RetryNotBeforeUtc is DateTimeOffset retryAfter && retryAfter > due)
            {
                due = retryAfter;
            }

            this._failures[snapshot.ProviderId] = new FailureState(
                failures, confirm ? due : null, snapshot.RetryNotBeforeUtc);
        }
    }

    public bool CanRefresh(ProviderId providerId, DateTimeOffset now)
    {
        lock (this._gate)
        {
            return this._failures.GetValueOrDefault(providerId)?.RetryNotBeforeUtc is not DateTimeOffset deadline
                || now >= deadline;
        }
    }

    public IReadOnlyCollection<ProviderId> TakeDueProviders(IEnumerable<ProviderId> enabledProviders, DateTimeOffset now)
    {
        HashSet<ProviderId> enabled = enabledProviders.ToHashSet();
        lock (this._gate)
        {
            List<ProviderId> due = [];
            foreach ((ProviderId providerId, FailureState state) in this._failures.ToArray())
            {
                if (!enabled.Contains(providerId))
                {
                    this._failures.Remove(providerId);
                }
                else if (state.DueAt is DateTimeOffset deadline && now >= deadline)
                {
                    due.Add(providerId);
                    this._failures[providerId] = state with { DueAt = null };
                }
            }

            return due;
        }
    }

    private sealed record FailureState(int Count, DateTimeOffset? DueAt, DateTimeOffset? RetryNotBeforeUtc);
}
