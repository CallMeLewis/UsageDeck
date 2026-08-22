using UsageDeck.Core.Providers;

namespace UsageDeck.App;

internal sealed class UsageRefreshScheduler(
    Func<ProviderId, CancellationToken, Task> refresh,
    CancellationToken lifetimeCancellation)
{
    private readonly object _gate = new();
    private readonly Dictionary<ProviderId, ProviderRefreshState> _states = [];

    public Task RefreshAutomaticallyAsync(
        IEnumerable<ProviderId> providerIds,
        CancellationToken cancellationToken = default) =>
        this.RequestAsync(providerIds, ensureRefreshAfterRequest: false, cancellationToken);

    public Task RefreshNowAsync(
        IEnumerable<ProviderId> providerIds,
        CancellationToken cancellationToken = default) =>
        this.RequestAsync(providerIds, ensureRefreshAfterRequest: true, cancellationToken);

    private Task RequestAsync(
        IEnumerable<ProviderId> providerIds,
        bool ensureRefreshAfterRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providerIds);

        Task[] refreshes = providerIds
            .Where(providerId => providerId != ProviderId.All)
            .Distinct()
            .Select(providerId => this.RequestProvider(providerId, ensureRefreshAfterRequest))
            .ToArray();
        Task requestedRefreshes = Task.WhenAll(refreshes);
        return cancellationToken.CanBeCanceled
            ? requestedRefreshes.WaitAsync(cancellationToken)
            : requestedRefreshes;
    }

    private Task RequestProvider(ProviderId providerId, bool ensureRefreshAfterRequest)
    {
        ProviderRefreshState state;
        Task completion;
        bool startRunner = false;
        lock (this._gate)
        {
            if (!this._states.TryGetValue(providerId, out state!))
            {
                state = new ProviderRefreshState();
                this._states.Add(providerId, state);
                completion = state.CurrentCompletion.Task;
                startRunner = true;
            }
            else if (ensureRefreshAfterRequest)
            {
                state.NextCompletion ??= CreateCompletion();
                completion = state.NextCompletion.Task;
            }
            else
            {
                completion = state.CurrentCompletion.Task;
            }
        }

        if (startRunner)
        {
            _ = this.RunAsync(providerId, state);
        }

        return completion;
    }

    private async Task RunAsync(ProviderId providerId, ProviderRefreshState state)
    {
        while (true)
        {
            Exception? failure = null;
            try
            {
                await refresh(providerId, lifetimeCancellation).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            bool runAgain;
            lock (this._gate)
            {
                Complete(state.CurrentCompletion, failure);
                if (lifetimeCancellation.IsCancellationRequested)
                {
                    state.NextCompletion?.TrySetCanceled(lifetimeCancellation);
                    this._states.Remove(providerId);
                    return;
                }

                runAgain = state.NextCompletion is not null;
                if (runAgain)
                {
                    state.CurrentCompletion = state.NextCompletion!;
                    state.NextCompletion = null;
                }
                else
                {
                    this._states.Remove(providerId);
                }
            }

            if (!runAgain)
            {
                return;
            }
        }
    }

    private static TaskCompletionSource CreateCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Complete(TaskCompletionSource completion, Exception? failure)
    {
        if (failure is OperationCanceledException cancellation)
        {
            completion.TrySetCanceled(cancellation.CancellationToken);
        }
        else if (failure is not null)
        {
            completion.TrySetException(failure);
        }
        else
        {
            completion.TrySetResult();
        }
    }

    private sealed class ProviderRefreshState
    {
        public TaskCompletionSource CurrentCompletion { get; set; } = CreateCompletion();

        public TaskCompletionSource? NextCompletion { get; set; }
    }
}
