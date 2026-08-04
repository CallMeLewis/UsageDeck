using UsageDeck.Core.Providers;

namespace UsageDeck.Core.Tests;

public sealed class ProviderRefreshCoordinatorTests
{
    [Fact]
    public async Task ConcurrentRefreshesShareOneProviderFetch()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeProvider provider = new(async cancellationToken =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return FreshSnapshot();
        });
        ProviderRefreshCoordinator coordinator = new([provider]);

        Task<ProviderSnapshot> first = coordinator.RefreshAsync(ProviderId.Codex);
        Task<ProviderSnapshot> second = coordinator.RefreshAsync(ProviderId.Codex);
        release.SetResult();

        ProviderSnapshot[] snapshots = await Task.WhenAll(first, second);

        Assert.Equal(1, provider.FetchCount);
        Assert.Same(snapshots[0], snapshots[1]);
    }

    [Fact]
    public async Task SimultaneousRefreshesStartOnlyOneProviderFetch()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeProvider provider = new(async cancellationToken =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return FreshSnapshot();
        });
        ProviderRefreshCoordinator coordinator = new([provider]);
        Task<ProviderSnapshot>[] refreshes = Enumerable.Range(0, 16)
            .Select(_ => coordinator.RefreshAsync(ProviderId.Codex))
            .ToArray();

        Assert.Equal(1, provider.FetchCount);

        release.SetResult();
        ProviderSnapshot[] snapshots = await Task.WhenAll(refreshes);

        Assert.All(snapshots, snapshot => Assert.Same(snapshots[0], snapshot));
    }

    [Fact]
    public async Task IndependentRefreshesShareTheGlobalConcurrencyLimit()
    {
        int active = 0;
        int highestActive = 0;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProviderId[] providerIds =
        [
            ProviderId.Codex,
            ProviderId.Claude,
            ProviderId.Antigravity,
            ProviderId.Copilot,
        ];
        FakeProvider[] providers = providerIds.Select(providerId => new FakeProvider(
            async cancellationToken =>
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
                return FreshSnapshot(providerId);
            },
            providerId: providerId)).ToArray();
        ProviderRefreshCoordinator coordinator = new(providers, maximumConcurrency: 2);

        Task<ProviderSnapshot>[] refreshes = providerIds
            .Select(providerId => coordinator.RefreshAsync(providerId))
            .ToArray();

        await WaitUntilAsync(() => Volatile.Read(ref active) == 2);
        Assert.Equal(2, Volatile.Read(ref highestActive));

        release.SetResult();
        await Task.WhenAll(refreshes);
        Assert.Equal(0, Volatile.Read(ref active));
    }

    [Fact]
    public async Task FastProviderWarningDoesNotFaultOtherConcurrentRefreshes()
    {
        FakeProvider[] providers =
        [
            new(
                _ => throw new ProviderException(
                    ProviderErrorCategory.AuthenticationRequired,
                    "Codex needs you to sign in.")),
            new(
                _ => Task.FromResult(FreshSnapshot(ProviderId.Claude)),
                providerId: ProviderId.Claude),
            new(
                _ => Task.FromResult(FreshSnapshot(ProviderId.Amp)),
                providerId: ProviderId.Amp),
        ];
        ProviderRefreshCoordinator coordinator = new(providers, maximumConcurrency: 2);

        ProviderSnapshot[] snapshots = await Task.WhenAll(providers.Select(provider =>
            coordinator.RefreshAsync(provider.Id)));

        Assert.Equal(3, snapshots.Length);
        ProviderSnapshot warning = Assert.Single(snapshots, snapshot => snapshot.ProviderId == ProviderId.Codex);
        Assert.Equal(UsageDataState.AuthenticationRequired, warning.State);
        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, warning.ErrorCategory);
        Assert.All(
            snapshots.Where(snapshot => snapshot.ProviderId != ProviderId.Codex),
            snapshot => Assert.Equal(UsageDataState.Fresh, snapshot.State));
    }

    [Fact]
    public async Task RefreshAfterCurrentRunsOnceMoreAndCoalescesCallers()
    {
        int fetchCount = 0;
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeProvider provider = new(async cancellationToken =>
        {
            if (Interlocked.Increment(ref fetchCount) == 1)
            {
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                secondStarted.SetResult();
            }

            return FreshSnapshot();
        });
        ProviderRefreshCoordinator coordinator = new([provider]);

        Task<ProviderSnapshot> current = coordinator.RefreshAsync(ProviderId.Codex);
        Task<ProviderSnapshot> queuedFirst = coordinator.RefreshAfterCurrentAsync(ProviderId.Codex);
        Task<ProviderSnapshot> queuedSecond = coordinator.RefreshAfterCurrentAsync(ProviderId.Codex);
        releaseFirst.SetResult();

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(current, queuedFirst, queuedSecond);

        Assert.Equal(2, provider.FetchCount);
    }

    [Fact]
    public async Task CancelledWaitDoesNotPreventTheNextRefresh()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstSnapshotPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeProvider provider = new(async _ =>
        {
            await release.Task;
            return FreshSnapshot();
        });
        ProviderRefreshCoordinator coordinator = new([provider]);
        coordinator.SnapshotChanged += (_, _) => firstSnapshotPublished.TrySetResult();
        using CancellationTokenSource cancellation = new();

        Task<ProviderSnapshot> cancelledRefresh = coordinator.RefreshAsync(
            ProviderId.Codex,
            cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRefresh);

        release.SetResult();
        await firstSnapshotPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.RefreshAsync(ProviderId.Codex);

        Assert.Equal(2, provider.FetchCount);
    }

    [Fact]
    public async Task FailedRefreshKeepsLastSnapshotAndMarksItStale()
    {
        Queue<Func<ProviderSnapshot>> results = new([
            () => FreshSnapshot(),
            () => throw new ProviderException(ProviderErrorCategory.Transient, "Codex is temporarily unavailable."),
        ]);
        FakeProvider provider = new(_ => Task.FromResult(results.Dequeue()()));
        ProviderRefreshCoordinator coordinator = new([provider]);

        ProviderSnapshot fresh = await coordinator.RefreshAsync(ProviderId.Codex);
        ProviderSnapshot stale = await coordinator.RefreshAsync(ProviderId.Codex);

        Assert.Equal(UsageDataState.Fresh, fresh.State);
        Assert.Equal(UsageDataState.Stale, stale.State);
        Assert.Equal(ProviderErrorCategory.Transient, stale.ErrorCategory);
        Assert.Equal(fresh.CapturedAt, stale.CapturedAt);
        Assert.Equal("Codex is temporarily unavailable.", stale.SafeError);
        Assert.Single(stale.UsageWindows);
        Assert.Equal(1, stale.ResetCredits?.AvailableCount);
    }

    [Fact]
    public async Task FailedInitialRefreshStillReportsTheInstalledCliVersion()
    {
        FakeProvider provider = new(
            _ => throw new ProviderException(
                ProviderErrorCategory.AuthenticationRequired,
                "Codex needs you to sign in."),
            cliVersion: "0.144.5");
        ProviderRefreshCoordinator coordinator = new([provider]);

        ProviderSnapshot snapshot = await coordinator.RefreshAsync(ProviderId.Codex);

        Assert.Equal(UsageDataState.AuthenticationRequired, snapshot.State);
        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, snapshot.ErrorCategory);
        Assert.Equal("0.144.5", snapshot.CliVersion);
    }

    [Fact]
    public async Task CliVersionCanBeReadWithoutRefreshingUsage()
    {
        FakeProvider provider = new(
            _ => Task.FromResult(FreshSnapshot()),
            cliVersion: "0.144.5");
        ProviderRefreshCoordinator coordinator = new([provider]);

        string? version = await coordinator.ReadCliVersionAsync(ProviderId.Codex);

        Assert.Equal("0.144.5", version);
        Assert.Equal(1, provider.VersionReadCount);
        Assert.Equal(0, provider.FetchCount);
    }

    private static ProviderSnapshot FreshSnapshot(ProviderId? providerId = null) => new(
        providerId ?? ProviderId.Codex,
        (providerId ?? ProviderId.Codex).DisplayName,
        "Native CLI",
        new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero),
        UsageDataState.Fresh,
        [new UsageWindow("session", "Session", 42)],
        resetCredits: new RateLimitResetCredits(1));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeProvider(
        Func<CancellationToken, Task<ProviderSnapshot>> fetch,
        string? cliVersion = null,
        ProviderId? providerId = null) : IUsageProvider, ICliVersionProvider
    {
        private int _fetchCount;
        private int _versionReadCount;

        public ProviderId Id => providerId ?? ProviderId.Codex;

        public string DisplayName => this.Id.DisplayName;

        public int FetchCount => this._fetchCount;

        public int VersionReadCount => this._versionReadCount;

        public Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this._fetchCount);
            return fetch(cancellationToken);
        }

        public Task<string?> ReadCliVersionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref this._versionReadCount);
            return Task.FromResult(cliVersion);
        }
    }
}
