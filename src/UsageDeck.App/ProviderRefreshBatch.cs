using UsageDeck.Core.Providers;

namespace UsageDeck.App;

internal static class ProviderRefreshBatch
{
    public static async Task RunAsync(
        IEnumerable<ProviderId> providerIds,
        int maximumConcurrency,
        Func<ProviderId, CancellationToken, Task> refresh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providerIds);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrency, 1);

        ProviderId[] providers = providerIds
            .Where(providerId => providerId != ProviderId.All)
            .Distinct()
            .ToArray();
        using SemaphoreSlim concurrency = new(maximumConcurrency, maximumConcurrency);
        Task[] refreshes = providers.Select(async providerId =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await refresh(providerId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        }).ToArray();

        await Task.WhenAll(refreshes).ConfigureAwait(false);
    }
}
