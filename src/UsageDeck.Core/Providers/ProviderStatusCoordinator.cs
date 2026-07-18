using System.Collections.Concurrent;

namespace UsageDeck.Core.Providers;

public sealed class ProviderStatusCoordinator
{
    private readonly ConcurrentDictionary<ProviderId, Lazy<Task<ProviderServiceStatusSnapshot>>> _inFlight = new();
    private readonly Dictionary<ProviderId, IProviderStatusProvider> _providers;
    private readonly ConcurrentDictionary<ProviderId, ProviderServiceStatusSnapshot> _snapshots = new();
    private readonly CancellationToken _shutdownToken;

    public ProviderStatusCoordinator(
        IEnumerable<IProviderStatusProvider> providers,
        CancellationToken shutdownToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        this._providers = providers.ToDictionary(provider => provider.Id);
        this._shutdownToken = shutdownToken;
    }

    public event EventHandler<ProviderServiceStatusSnapshot>? SnapshotChanged;

    public ProviderServiceStatusSnapshot? GetSnapshot(ProviderId providerId) =>
        this._snapshots.GetValueOrDefault(providerId);

    public Uri? GetOfficialStatusUri(ProviderId providerId)
    {
        if (!this._providers.TryGetValue(providerId, out IProviderStatusProvider? provider))
        {
            throw new KeyNotFoundException($"Provider '{providerId}' is not registered.");
        }

        return provider.OfficialStatusUri;
    }

    public async Task<ProviderServiceStatusSnapshot> RefreshAsync(
        ProviderId providerId,
        CancellationToken cancellationToken = default)
    {
        if (!this._providers.TryGetValue(providerId, out IProviderStatusProvider? provider))
        {
            throw new KeyNotFoundException($"Provider '{providerId}' is not registered.");
        }

        Lazy<Task<ProviderServiceStatusSnapshot>> lazyRefresh = this._inFlight.GetOrAdd(
            providerId,
            _ => new Lazy<Task<ProviderServiceStatusSnapshot>>(
                () => this.RefreshAndRemoveAsync(provider),
                LazyThreadSafetyMode.ExecutionAndPublication));
        Task<ProviderServiceStatusSnapshot> refreshTask = lazyRefresh.Value;
        return await refreshTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProviderServiceStatusSnapshot>> RefreshAsync(
        IEnumerable<ProviderId> providerIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerIds);
        Task<ProviderServiceStatusSnapshot>[] refreshes = providerIds
            .Distinct()
            .Select(providerId => this.RefreshAsync(providerId, cancellationToken))
            .ToArray();
        return await Task.WhenAll(refreshes).ConfigureAwait(false);
    }

    private async Task<ProviderServiceStatusSnapshot> RefreshAndRemoveAsync(IProviderStatusProvider provider)
    {
        try
        {
            return await this.RefreshCoreAsync(provider).ConfigureAwait(false);
        }
        finally
        {
            this._inFlight.TryRemove(provider.Id, out _);
        }
    }

    private async Task<ProviderServiceStatusSnapshot> RefreshCoreAsync(IProviderStatusProvider provider)
    {
        ProviderServiceStatusSnapshot snapshot;
        try
        {
            snapshot = await provider.FetchStatusAsync(this._shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (this._shutdownToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderStatusException exception)
        {
            snapshot = this.MakeFailureSnapshot(provider, exception.SafeMessage);
        }
        catch (Exception exception)
        {
            snapshot = this.MakeFailureSnapshot(
                provider,
                $"{provider.Id.DisplayName} status could not be refreshed.");
            System.Diagnostics.Debug.WriteLine(exception);
        }

        this._snapshots[provider.Id] = snapshot;
        this.SnapshotChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    private ProviderServiceStatusSnapshot MakeFailureSnapshot(
        IProviderStatusProvider provider,
        string safeError)
    {
        if (this._snapshots.TryGetValue(provider.Id, out ProviderServiceStatusSnapshot? previous))
        {
            return previous with { IsStale = true, SafeError = safeError };
        }

        return new ProviderServiceStatusSnapshot(
            provider.Id,
            ProviderServiceHealth.Unknown,
            safeError,
            null,
            provider.OfficialStatusUri,
            IsStale: true,
            SafeError: safeError);
    }
}
