using System.Text;
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
        FakeProcessRunner processes = new("Amp Free: 75% remaining today (resets daily)");
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
            FakeProcessRunner processes = new("Amp Free: 75% remaining today (resets daily)");
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
            new FakeProcessRunner("unused"),
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
            new FakeProcessRunner("unused"),
            new StubExecutableLocator("C:\\tools\\amp.exe"));
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.FetchAsync(cancellation.Token));
    }

    [Fact]
    public async Task FetchReportsANonZeroExitWithoutUsageAsUnavailable()
    {
        AmpUsageProvider provider = new(
            new FakeProcessRunner(string.Empty, exitCode: 1),
            new StubExecutableLocator("C:\\tools\\amp.exe"));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.Unavailable, exception.Category);
    }

    [Fact]
    public async Task FetchPreservesAuthenticationGuidanceFromANonZeroExit()
    {
        AmpUsageProvider provider = new(
            new FakeProcessRunner("You are not logged in.", exitCode: 1),
            new StubExecutableLocator("C:\\tools\\amp.exe"));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
    }

    private sealed class StubExecutableLocator(string? path) : IExecutableLocator
    {
        public string? FindExecutable(string executableName) => path;
    }

    private sealed class FakeProcessRunner(string response, int exitCode = 0) : IBoundedProcessRunner
    {
        public ProcessStartSpec? StartSpec { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            ProcessStartSpec spec,
            int maximumStandardOutputBytes,
            int maximumStandardErrorBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.StartSpec = spec;
            return Task.FromResult(new ProcessRunResult(
                Encoding.UTF8.GetBytes(response),
                exitCode,
                string.Empty));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
