using System.Collections.Concurrent;

namespace UsageDeck.Core.Providers;

public sealed class ProviderRefreshCoordinator
{
    private readonly IReadOnlyDictionary<ProviderId, IUsageProvider> _providers;
    private readonly ConcurrentDictionary<ProviderId, ProviderSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<ProviderId, Lazy<Task<ProviderSnapshot>>> _inFlight = new();
    private readonly CancellationToken _shutdownToken;

    public ProviderRefreshCoordinator(IEnumerable<IUsageProvider> providers, CancellationToken shutdownToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        this._providers = providers.ToDictionary(provider => provider.Id);
        this._shutdownToken = shutdownToken;
    }

    public event EventHandler<ProviderSnapshot>? SnapshotChanged;

    public IReadOnlyCollection<ProviderId> ProviderIds => this._providers.Keys.ToArray();

    public ProviderSnapshot? GetSnapshot(ProviderId providerId) =>
        this._snapshots.GetValueOrDefault(providerId);

    public async Task<string?> ReadCliVersionAsync(
        ProviderId providerId,
        CancellationToken cancellationToken = default)
    {
        if (!this._providers.TryGetValue(providerId, out IUsageProvider? provider))
        {
            throw new KeyNotFoundException($"Provider '{providerId}' is not registered.");
        }

        return await this.ReadCliVersionSafelyAsync(provider)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ProviderSnapshot> RefreshAsync(
        ProviderId providerId,
        CancellationToken cancellationToken = default)
    {
        if (!this._providers.TryGetValue(providerId, out IUsageProvider? provider))
        {
            throw new KeyNotFoundException($"Provider '{providerId}' is not registered.");
        }

        Lazy<Task<ProviderSnapshot>> lazyRefresh = this._inFlight.GetOrAdd(
            providerId,
            _ => new Lazy<Task<ProviderSnapshot>>(
                () => this.RefreshCoreAsync(provider),
                LazyThreadSafetyMode.ExecutionAndPublication));
        Task<ProviderSnapshot> refreshTask = lazyRefresh.Value;

        try
        {
            return await refreshTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (refreshTask.IsCompleted)
            {
                this._inFlight.TryRemove(
                    new KeyValuePair<ProviderId, Lazy<Task<ProviderSnapshot>>>(providerId, lazyRefresh));
            }
        }
    }

    public async Task<IReadOnlyList<ProviderSnapshot>> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        Task<ProviderSnapshot>[] tasks = this._providers.Keys
            .Select(providerId => this.RefreshAsync(providerId, cancellationToken))
            .ToArray();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ProviderSnapshot> RefreshCoreAsync(IUsageProvider provider)
    {
        ProviderSnapshot snapshot;
        Task<string?> cliVersionTask = this.ReadCliVersionSafelyAsync(provider);

        try
        {
            snapshot = await provider.FetchAsync(this._shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (this._shutdownToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderException exception)
        {
            snapshot = this.MakeFailureSnapshot(provider, exception.Category, exception.SafeMessage);
        }
        catch (Exception exception)
        {
            snapshot = this.MakeFailureSnapshot(
                provider,
                ProviderErrorCategory.Unavailable,
                $"{provider.DisplayName} usage could not be refreshed.");

            System.Diagnostics.Debug.WriteLine(exception);
        }

        string? cliVersion = await cliVersionTask.ConfigureAwait(false);
        if (cliVersion is not null)
        {
            snapshot = snapshot.WithCliVersion(cliVersion);
        }

        this._snapshots[provider.Id] = snapshot;
        this.SnapshotChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    private ProviderSnapshot MakeFailureSnapshot(
        IUsageProvider provider,
        ProviderErrorCategory category,
        string safeMessage)
    {
        if (this._snapshots.TryGetValue(provider.Id, out ProviderSnapshot? previous))
        {
            return previous.WithFailure(UsageDataState.Stale, safeMessage, category);
        }

        UsageDataState state = category == ProviderErrorCategory.AuthenticationRequired
            ? UsageDataState.AuthenticationRequired
            : UsageDataState.Unavailable;

        return new ProviderSnapshot(
            provider.Id,
            provider.DisplayName,
            "No source",
            DateTimeOffset.MinValue,
            state,
            safeError: safeMessage,
            errorCategory: category);
    }

    private async Task<string?> ReadCliVersionSafelyAsync(IUsageProvider provider)
    {
        if (provider is not ICliVersionProvider versionProvider)
        {
            return null;
        }

        try
        {
            return await versionProvider.ReadCliVersionAsync(this._shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (this._shutdownToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // CLI version is optional metadata and must not prevent authoritative usage data from loading.
            System.Diagnostics.Debug.WriteLine(exception);
            return null;
        }
    }
}
