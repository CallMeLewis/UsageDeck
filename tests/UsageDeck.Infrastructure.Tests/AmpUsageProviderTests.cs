using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Providers.Amp;

namespace UsageDeck.Infrastructure.Tests;

public sealed class AmpUsageProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FetchRunsReadOnlyUsageCommand()
    {
        FakeProcessSessionFactory processes = new("Amp Free: 75% remaining today (resets daily)");
        AmpUsageProvider provider = new(
            processes,
            new StubExecutableLocator("C:\\tools\\amp.exe"),
            new FixedTimeProvider(Now));

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderId.Amp, snapshot.ProviderId);
        Assert.Equal(25, Assert.Single(snapshot.UsageWindows).UsedPercent);
        Assert.Equal("C:\\tools\\amp.exe", processes.StartSpec?.ExecutablePath);
        Assert.Equal(["usage"], processes.StartSpec?.Arguments);
        Assert.Equal("1", processes.StartSpec?.Environment?["NO_COLOR"]);
        Assert.Equal("dumb", processes.StartSpec?.Environment?["TERM"]);
    }

    [Fact]
    public async Task FetchFindsTheStandardAmpInstallerLocation()
    {
        string profile = Path.Combine(Path.GetTempPath(), $"UsageDeck-Amp-{Guid.NewGuid():N}");
        string executable = Path.Combine(profile, ".amp", "bin", "amp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        await File.WriteAllBytesAsync(executable, []);
        try
        {
            FakeProcessSessionFactory processes = new("Amp Free: 75% remaining today (resets daily)");
            AmpUsageProvider provider = new(
                processes,
                new StubExecutableLocator(null),
                new FixedTimeProvider(Now),
                profile);

            _ = await provider.FetchAsync(CancellationToken.None);

            Assert.Equal(executable, processes.StartSpec?.ExecutablePath);
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    [Fact]
    public async Task FetchReportsWhenAmpIsNotInstalled()
    {
        string profile = Path.Combine(Path.GetTempPath(), $"UsageDeck-Amp-{Guid.NewGuid():N}");
        AmpUsageProvider provider = new(
            new FakeProcessSessionFactory("unused"),
            new StubExecutableLocator(null),
            userProfile: profile);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.NotInstalled, exception.Category);
    }

    [Fact]
    public async Task FetchPreservesCallerCancellation()
    {
        AmpUsageProvider provider = new(
            new FakeProcessSessionFactory("unused"),
            new StubExecutableLocator("C:\\tools\\amp.exe"));
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.FetchAsync(cancellation.Token));
    }

    private sealed class StubExecutableLocator(string? path) : IExecutableLocator
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

        public Task WriteLineAsync(string line, CancellationToken cancellationToken) => Task.CompletedTask;

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
