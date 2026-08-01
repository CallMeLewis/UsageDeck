using UsageDeck.Core.Providers;

namespace UsageDeck.App.Tests;

public sealed class ProviderRefreshBatchTests
{
    [Fact]
    public async Task RunAsyncRefreshesEveryDistinctProviderExceptAll()
    {
        List<ProviderId> refreshed = [];

        await ProviderRefreshBatch.RunAsync(
            [ProviderId.Codex, ProviderId.Claude, ProviderId.Codex, ProviderId.All],
            maximumConcurrency: 2,
            (providerId, _) =>
            {
                lock (refreshed)
                {
                    refreshed.Add(providerId);
                }

                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal([ProviderId.Claude, ProviderId.Codex], refreshed.OrderBy(id => id.Value));
    }

    [Fact]
    public async Task RunAsyncLimitsConcurrentProviderWork()
    {
        int active = 0;
        int highestActive = 0;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task batch = ProviderRefreshBatch.RunAsync(
            [ProviderId.Codex, ProviderId.Claude, ProviderId.Antigravity, ProviderId.Copilot],
            maximumConcurrency: 2,
            async (_, cancellationToken) =>
            {
                int current = Interlocked.Increment(ref active);
                int observed;
                do
                {
                    observed = Volatile.Read(ref highestActive);
                }
                while (current > observed
                    && Interlocked.CompareExchange(ref highestActive, current, observed) != observed);

                await release.Task.WaitAsync(cancellationToken);
                Interlocked.Decrement(ref active);
            },
            CancellationToken.None);

        await WaitUntilAsync(() => Volatile.Read(ref active) == 2);
        Assert.Equal(2, Volatile.Read(ref highestActive));

        release.SetResult();
        await batch;
        Assert.Equal(0, Volatile.Read(ref active));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
