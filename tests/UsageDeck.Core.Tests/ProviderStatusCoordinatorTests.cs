using UsageDeck.Core.Providers;

namespace UsageDeck.Core.Tests;

public sealed class ProviderStatusCoordinatorTests
{
    [Fact]
    public async Task RefreshCachesAndPublishesProviderStatus()
    {
        ProviderServiceStatusSnapshot expected = new(
            ProviderId.Codex,
            ProviderServiceHealth.ProblemsReported,
            "Codex is degraded.",
            DateTimeOffset.UtcNow,
            new Uri("https://status.openai.com/"));
        RecordingStatusProvider provider = new(ProviderId.Codex, _ => Task.FromResult(expected));
        ProviderStatusCoordinator coordinator = new([provider]);
        ProviderServiceStatusSnapshot? published = null;
        coordinator.SnapshotChanged += (_, snapshot) => published = snapshot;

        ProviderServiceStatusSnapshot actual = await coordinator.RefreshAsync(ProviderId.Codex);

        Assert.Equal(expected, actual);
        Assert.Equal(expected, coordinator.GetSnapshot(ProviderId.Codex));
        Assert.Equal(expected, published);
    }

    [Fact]
    public async Task FailedRefreshRetainsTheLastKnownIncidentAsStale()
    {
        int attempt = 0;
        ProviderServiceStatusSnapshot incident = new(
            ProviderId.Claude,
            ProviderServiceHealth.ProblemsReported,
            "Claude Code errors",
            DateTimeOffset.UtcNow,
            new Uri("https://status.claude.com/"));
        RecordingStatusProvider provider = new(ProviderId.Claude, _ =>
        {
            attempt++;
            return attempt == 1
                ? Task.FromResult(incident)
                : Task.FromException<ProviderServiceStatusSnapshot>(
                    new ProviderStatusException("Claude status could not be refreshed."));
        });
        ProviderStatusCoordinator coordinator = new([provider]);

        await coordinator.RefreshAsync(ProviderId.Claude);
        ProviderServiceStatusSnapshot stale = await coordinator.RefreshAsync(ProviderId.Claude);

        Assert.True(stale.HasProblems);
        Assert.True(stale.IsStale);
        Assert.Equal(incident.Summary, stale.Summary);
        Assert.Equal("Claude status could not be refreshed.", stale.SafeError);
    }

    [Fact]
    public async Task ConcurrentRefreshesShareOneProviderRequest()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingStatusProvider provider = new(ProviderId.Copilot, async _ =>
        {
            await release.Task;
            return new ProviderServiceStatusSnapshot(
                ProviderId.Copilot,
                ProviderServiceHealth.Operational,
                "No problems reported.",
                DateTimeOffset.UtcNow);
        });
        ProviderStatusCoordinator coordinator = new([provider]);

        Task<ProviderServiceStatusSnapshot> first = coordinator.RefreshAsync(ProviderId.Copilot);
        Task<ProviderServiceStatusSnapshot> second = coordinator.RefreshAsync(ProviderId.Copilot);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, provider.FetchCount);
    }

    [Fact]
    public async Task CancelledWaitDoesNotPreventTheNextRefresh()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstSnapshotPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingStatusProvider provider = new(ProviderId.Amp, async _ =>
        {
            await release.Task;
            return new ProviderServiceStatusSnapshot(
                ProviderId.Amp,
                ProviderServiceHealth.Operational,
                "No problems reported.",
                DateTimeOffset.UtcNow);
        });
        ProviderStatusCoordinator coordinator = new([provider]);
        coordinator.SnapshotChanged += (_, _) => firstSnapshotPublished.TrySetResult();
        using CancellationTokenSource cancellation = new();

        Task<ProviderServiceStatusSnapshot> cancelledRefresh = coordinator.RefreshAsync(
            ProviderId.Amp,
            cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRefresh);

        release.SetResult();
        await firstSnapshotPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.RefreshAsync(ProviderId.Amp);

        Assert.Equal(2, provider.FetchCount);
    }

    private sealed class RecordingStatusProvider(
        ProviderId id,
        Func<CancellationToken, Task<ProviderServiceStatusSnapshot>> fetch) : IProviderStatusProvider
    {
        private int _fetchCount;

        public ProviderId Id { get; } = id;

        public Uri? OfficialStatusUri => new("https://status.example.com/");

        public int FetchCount => this._fetchCount;

        public Task<ProviderServiceStatusSnapshot> FetchStatusAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this._fetchCount);
            return fetch(cancellationToken);
        }
    }
}
