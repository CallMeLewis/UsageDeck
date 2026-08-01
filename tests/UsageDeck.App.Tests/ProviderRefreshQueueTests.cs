using UsageDeck.Core.Providers;

namespace UsageDeck.App.Tests;

public sealed class ProviderRefreshQueueTests
{
    [Fact]
    public async Task RequestMadeDuringRefreshRunsAfterTheCurrentBatch()
    {
        List<ProviderId> refreshed = [];
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProviderRefreshQueue queue = new(
            maximumConcurrency: 2,
            async (providerId, cancellationToken) =>
            {
                lock (refreshed)
                {
                    refreshed.Add(providerId);
                }

                if (providerId == ProviderId.Codex)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    secondCompleted.SetResult();
                }
            },
            CancellationToken.None);

        queue.Request([ProviderId.Codex]);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Request([ProviderId.Claude]);
        releaseFirst.SetResult();
        await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([ProviderId.Codex, ProviderId.Claude], refreshed);
    }

    [Fact]
    public async Task RepeatedPendingRequestsForOneProviderAreCoalesced()
    {
        int refreshCount = 0;
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProviderRefreshQueue queue = new(
            maximumConcurrency: 2,
            async (_, cancellationToken) =>
            {
                int current = Interlocked.Increment(ref refreshCount);
                if (current == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    secondCompleted.SetResult();
                }
            },
            CancellationToken.None);

        queue.Request([ProviderId.Codex]);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Request([ProviderId.Codex]);
        queue.Request([ProviderId.Codex]);
        releaseFirst.SetResult();
        await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, Volatile.Read(ref refreshCount));
    }
}
