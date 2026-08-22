using UsageDeck.Core.Providers;

namespace UsageDeck.App.Tests;

public sealed class UsageRefreshSchedulerTests
{
    [Fact]
    public async Task AutomaticRequestDuringRefreshDoesNotQueueCatchUpWork()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshCount = 0;
        UsageRefreshScheduler scheduler = new(
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref refreshCount);
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
            },
            CancellationToken.None);

        Task first = scheduler.RefreshAutomaticallyAsync([ProviderId.Codex]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task second = scheduler.RefreshAutomaticallyAsync([ProviderId.Codex]);

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, Volatile.Read(ref refreshCount));
    }

    [Fact]
    public async Task GuaranteedRequestDuringFollowUpRunsAgain()
    {
        TaskCompletionSource[] started = CreateSignals(3);
        TaskCompletionSource[] releases = CreateSignals(3);
        int refreshCount = 0;
        UsageRefreshScheduler scheduler = new(
            async (_, cancellationToken) =>
            {
                int refreshIndex = Interlocked.Increment(ref refreshCount) - 1;
                started[refreshIndex].SetResult();
                await releases[refreshIndex].Task.WaitAsync(cancellationToken);
            },
            CancellationToken.None);

        Task first = scheduler.RefreshNowAsync([ProviderId.Codex]);
        await started[0].Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task second = scheduler.RefreshNowAsync([ProviderId.Codex]);

        releases[0].SetResult();
        await started[1].Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task third = scheduler.RefreshNowAsync([ProviderId.Codex]);

        releases[1].SetResult();
        await started[2].Task.WaitAsync(TimeSpan.FromSeconds(2));
        releases[2].SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(3, Volatile.Read(ref refreshCount));
    }

    private static TaskCompletionSource[] CreateSignals(int count) => Enumerable.Range(0, count)
        .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
        .ToArray();
}
