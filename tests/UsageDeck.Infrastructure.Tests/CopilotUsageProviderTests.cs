using System.Text;
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
        FakeProcessRunner sessions = new(response);
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
        FakeProcessRunner sessions = new("unused");
        CopilotUsageProvider provider = new(
            sessions,
            new StubExecutableLocator("C:\\tools\\gh.exe"));
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.FetchAsync(cancellation.Token));
    }

    [Fact]
    public async Task FetchReportsANonZeroExitWithoutUsageAsUnavailable()
    {
        CopilotUsageProvider provider = new(
            new FakeProcessRunner(string.Empty, exitCode: 1),
            new StubExecutableLocator("C:\\tools\\gh.exe"));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.Unavailable, exception.Category);
    }

    [Fact]
    public async Task FetchPreservesAuthenticationGuidanceFromANonZeroExit()
    {
        CopilotUsageProvider provider = new(
            new FakeProcessRunner("{\"message\":\"authentication required\"}", exitCode: 1),
            new StubExecutableLocator("C:\\tools\\gh.exe"));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
    }

    private sealed class StubExecutableLocator(string path) : IExecutableLocator
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
