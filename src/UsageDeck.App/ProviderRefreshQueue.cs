using System.Diagnostics;
using UsageDeck.Core.Providers;

namespace UsageDeck.App;

internal sealed class ProviderRefreshQueue(
    int maximumConcurrency,
    Func<ProviderId, CancellationToken, Task> refresh,
    CancellationToken cancellationToken)
{
    private readonly object _gate = new();
    private readonly HashSet<ProviderId> _pending = [];
    private bool _isRunning;

    public void Request(IEnumerable<ProviderId> providerIds)
    {
        ArgumentNullException.ThrowIfNull(providerIds);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        bool startRunner = false;
        lock (this._gate)
        {
            foreach (ProviderId providerId in providerIds.Where(providerId => providerId != ProviderId.All))
            {
                this._pending.Add(providerId);
            }

            if (this._pending.Count > 0 && !this._isRunning)
            {
                this._isRunning = true;
                startRunner = true;
            }
        }

        if (startRunner)
        {
            _ = this.RunAsync();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                ProviderId[] providers;
                lock (this._gate)
                {
                    if (this._pending.Count == 0)
                    {
                        return;
                    }

                    providers = this._pending.ToArray();
                    this._pending.Clear();
                }

                await ProviderRefreshBatch.RunAsync(
                    providers,
                    maximumConcurrency,
                    refresh,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Queued provider refresh stopped unexpectedly: {exception.GetType().Name}.");
        }
        finally
        {
            bool restartRunner;
            lock (this._gate)
            {
                this._isRunning = false;
                restartRunner = this._pending.Count > 0 && !cancellationToken.IsCancellationRequested;
                if (restartRunner)
                {
                    this._isRunning = true;
                }
            }

            if (restartRunner)
            {
                _ = this.RunAsync();
            }
        }
    }
}
