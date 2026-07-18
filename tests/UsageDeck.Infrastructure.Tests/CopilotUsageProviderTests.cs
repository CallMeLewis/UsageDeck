using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Providers.Copilot;

namespace UsageDeck.Infrastructure.Tests;

public sealed class CopilotUsageProviderTests
{
    [Fact]
    public async Task FetchUsesGitHubCliWithoutReadingOrPassingAToken()
    {
        const string response = """
            {"copilot_plan":"individual","quota_snapshots":{"chat":{"has_quota":true,"percent_remaining":75,"unlimited":false}}}
            """;
        FakeProcessSessionFactory sessions = new(response);
        CopilotUsageProvider provider = new(
            sessions,
            new StubExecutableLocator("C:\\tools\\gh.exe"),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero)));

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderId.Copilot, snapshot.ProviderId);
        Assert.Equal(25, Assert.Single(snapshot.UsageWindows).UsedPercent);
        Assert.Equal("C:\\tools\\gh.exe", sessions.StartSpec?.ExecutablePath);
        Assert.Contains("/copilot_internal/user", sessions.StartSpec?.Arguments ?? []);
        Assert.DoesNotContain(
            sessions.StartSpec?.Arguments ?? [],
            argument => argument.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("1", sessions.StartSpec?.Environment?["GH_PROMPT_DISABLED"]);
    }

    [Fact]
    public async Task FetchPreservesCallerCancellation()
    {
        FakeProcessSessionFactory sessions = new("unused");
        CopilotUsageProvider provider = new(
            sessions,
            new StubExecutableLocator("C:\\tools\\gh.exe"));
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.FetchAsync(cancellation.Token));
    }

    private sealed class StubExecutableLocator(string path) : IExecutableLocator
    {
        public string? FindExecutable(string executableName) => path;
    }

    private sealed class FakeProcessSessionFactory(string response) : IProcessSessionFactory
    {
        public ProcessStartSpec? StartSpec { get; private set; }

        public IProcessSession Start(ProcessStartSpec spec)
        {
            this.StartSpec = spec;
            return new FakeProcessSession(response);
        }
    }

    private sealed class FakeProcessSession(string response) : IProcessSession
    {
        private string? _response = response;

        public Task WriteLineAsync(string line, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? result = this._response;
            this._response = null;
            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
