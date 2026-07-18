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
    public async Task FailedRefreshKeepsLastSnapshotAndMarksItStale()
    {
        Queue<Func<ProviderSnapshot>> results = new([
            FreshSnapshot,
            () => throw new ProviderException(ProviderErrorCategory.Transient, "Codex is temporarily unavailable."),
        ]);
        FakeProvider provider = new(_ => Task.FromResult(results.Dequeue()()));
        ProviderRefreshCoordinator coordinator = new([provider]);

        ProviderSnapshot fresh = await coordinator.RefreshAsync(ProviderId.Codex);
        ProviderSnapshot stale = await coordinator.RefreshAsync(ProviderId.Codex);

        Assert.Equal(UsageDataState.Fresh, fresh.State);
        Assert.Equal(UsageDataState.Stale, stale.State);
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

    private static ProviderSnapshot FreshSnapshot() => new(
        ProviderId.Codex,
        "Codex",
        "Native CLI",
        new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero),
        UsageDataState.Fresh,
        [new UsageWindow("session", "Session", 42)],
        resetCredits: new RateLimitResetCredits(1));

    private sealed class FakeProvider(
        Func<CancellationToken, Task<ProviderSnapshot>> fetch,
        string? cliVersion = null) : IUsageProvider, ICliVersionProvider
    {
        private int _fetchCount;
        private int _versionReadCount;

        public ProviderId Id => ProviderId.Codex;

        public string DisplayName => "Codex";

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
