using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Processes;
using CodexBarWin.Infrastructure.Providers.Codex;

namespace CodexBarWin.Infrastructure.Tests;

public sealed class CodexUsageProviderTests
{
    [Fact]
    public async Task FetchMapsPrimaryWeeklyAdditionalCreditsAndIdentity()
    {
        FakeProcessSession session = new([
            "{\"id\":1,\"result\":{}}",
            "{\"method\":\"account/updated\",\"params\":{}}",
            "{\"id\":2,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":42,\"windowDurationMins\":300,\"resetsAt\":1784204040},\"secondary\":{\"usedPercent\":58,\"windowDurationMins\":10080,\"resetsAt\":1784620800},\"credits\":{\"hasCredits\":true,\"unlimited\":false,\"balance\":\"12.50\"},\"planType\":\"plus\"},\"rateLimitsByLimitId\":{\"codex-spark\":{\"limitName\":\"Codex Spark\",\"primary\":{\"usedPercent\":21,\"windowDurationMins\":300,\"resetsAt\":1784204040}}},\"rateLimitResetCredits\":{\"availableCount\":1,\"credits\":[{\"id\":\"opaque-credit-id\",\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"grantedAt\":1784200000,\"expiresAt\":1786556217}]}}}",
            "{\"id\":3,\"result\":{\"account\":{\"type\":\"chatgpt\",\"email\":\"developer@example.com\",\"planType\":\"plus\"}}}",
        ]);
        FixedProcessSessionFactory sessionFactory = new(session);
        CodexUsageProvider provider = CreateProvider(sessionFactory);

        ProviderSnapshot result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(UsageDataState.Fresh, result.State);
        Assert.Equal("Native CLI", result.SourceDescription);
        Assert.Equal(3, result.UsageWindows.Count);
        Assert.Collection(
            result.UsageWindows,
            window => Assert.Equal(("session", "Session", 42d), (window.Id, window.DisplayName, window.UsedPercent)),
            window => Assert.Equal(("weekly", "Weekly", 58d), (window.Id, window.DisplayName, window.UsedPercent)),
            window => Assert.Equal(("codex-spark", "Codex Spark", 21d), (window.Id, window.DisplayName, window.UsedPercent)));
        Assert.Equal("developer@example.com", result.Identity?.Email);
        Assert.Equal("plus", result.Identity?.Plan);
        Assert.Equal("12.50", result.Credits?.Balance);
        Assert.Equal(1, result.ResetCredits?.AvailableCount);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1786556217),
            Assert.Single(result.ResetCredits!.Credits).ExpiresAt);
        Assert.Equal(4, session.WrittenLines.Count);
        Assert.DoesNotContain(session.WrittenLines, line => line.Contains("developer@example.com", StringComparison.Ordinal));
        Assert.DoesNotContain(session.WrittenLines, line => line.Contains("opaque-credit-id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchMapsSnakeCaseResetCreditSummaryWithoutDetails()
    {
        FakeProcessSession session = new([
            "{\"id\":1,\"result\":{}}",
            "{\"id\":2,\"result\":{\"rate_limits\":{\"primary\":{\"used_percent\":12}},\"rate_limit_reset_credits\":{\"available_count\":2,\"credits\":[{\"expires_at\":1787000000}]}}}",
            "{\"id\":3,\"result\":{}}",
        ]);
        CodexUsageProvider provider = CreateProvider(new FixedProcessSessionFactory(session));

        ProviderSnapshot result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(2, result.ResetCredits?.AvailableCount);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1787000000),
            Assert.Single(result.ResetCredits!.Credits).ExpiresAt);
    }

    [Fact]
    public async Task FetchMapsAuthenticationErrorsToSafeGuidance()
    {
        FakeProcessSession session = new([
            "{\"id\":1,\"result\":{}}",
            "{\"id\":2,\"error\":{\"code\":401,\"message\":\"Not authenticated: secret-token-value\"}}",
        ]);
        CodexUsageProvider provider = CreateProvider(new FixedProcessSessionFactory(session));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
        Assert.Equal("Codex needs you to sign in. Run `codex login`, then refresh.", exception.SafeMessage);
        Assert.DoesNotContain("secret-token-value", exception.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void WslProcessSpecUsesFixedArgumentsWithoutAShell()
    {
        CodexProcessSpecFactory factory = new(new StubExecutableLocator(null));

        ProcessStartSpec spec = factory.Create(ProviderHost.Wsl("Ubuntu Dev"));

        Assert.EndsWith("wsl.exe", spec.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["--distribution", "Ubuntu Dev", "--exec", "codex", "-s", "read-only", "-a", "untrusted", "app-server"],
            spec.Arguments);
    }

    [Fact]
    public void WslVersionSpecUsesTheProviderDistributionWithoutAShell()
    {
        CodexProcessSpecFactory factory = new(new StubExecutableLocator(null));

        ProcessStartSpec spec = factory.CreateVersion(ProviderHost.Wsl("Ubuntu Dev"));

        Assert.EndsWith("wsl.exe", spec.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["--distribution", "Ubuntu Dev", "--exec", "codex", "--version"],
            spec.Arguments);
    }

    private static CodexUsageProvider CreateProvider(IProcessSessionFactory sessionFactory) => new(
        sessionFactory,
        new CodexProcessSpecFactory(new StubExecutableLocator("C:\\Tools\\codex.exe")),
        ProviderHost.Native,
        new FixedTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero)));

    private sealed class StubExecutableLocator(string? path) : IExecutableLocator
    {
        public string? FindExecutable(string executableName) => path;
    }

    private sealed class FixedProcessSessionFactory(IProcessSession session) : IProcessSessionFactory
    {
        public IProcessSession Start(ProcessStartSpec spec) => session;
    }

    private sealed class FakeProcessSession(IEnumerable<string> lines) : IProcessSession
    {
        private readonly Queue<string> _lines = new(lines);

        public List<string> WrittenLines { get; } = [];

        public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.WrittenLines.Add(line);
            return Task.CompletedTask;
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(this._lines.TryDequeue(out string? line) ? line : null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
