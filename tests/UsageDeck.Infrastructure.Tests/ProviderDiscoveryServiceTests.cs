using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Providers;

namespace UsageDeck.Infrastructure.Tests;

public sealed class ProviderDiscoveryServiceTests
{
    [Fact]
    public void DiscoverReportsDetectedCliWithoutExposingItsPath()
    {
        FakeExecutableLocator locator = new(new Dictionary<string, string>
        {
            ["codex"] = @"C:\Tools\codex.exe",
            ["gh"] = @"C:\Tools\gh.exe",
        });
        ProviderDiscoveryService service = new(locator, () => null);

        IReadOnlyList<ProviderDiscoveryResult> results = service.Discover();

        Assert.Equal(ProviderId.Supported, results.Select(result => result.ProviderId));
        Assert.Equal(
            ProviderDiscoveryState.Detected,
            results.Single(result => result.ProviderId == ProviderId.Codex).State);
        Assert.Equal(
            ProviderDiscoveryState.Detected,
            results.Single(result => result.ProviderId == ProviderId.Copilot).State);
        Assert.Equal(
            ProviderDiscoveryState.NotDetected,
            results.Single(result => result.ProviderId == ProviderId.Claude).State);
        Assert.DoesNotContain(
            results,
            result => result.Detail.Contains(@"C:\Tools", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiscoverReportsOpenCodeLocalHistoryAsDetected()
    {
        ProviderDiscoveryService service = new(
            new FakeExecutableLocator(new Dictionary<string, string>()),
            () => @"C:\Users\Test\opencode.db");

        ProviderDiscoveryResult result = service.Discover()
            .Single(value => value.ProviderId == ProviderId.OpenCodeGo);

        Assert.Equal(ProviderDiscoveryState.Detected, result.State);
    }

    [Fact]
    public void DiscoverKeepsApiProvidersAvailableForManualSetup()
    {
        ProviderDiscoveryService service = new(
            new FakeExecutableLocator(new Dictionary<string, string>()),
            () => null);

        IReadOnlyList<ProviderDiscoveryResult> results = service.Discover();

        Assert.Equal(
            ProviderDiscoveryState.RequiresSetup,
            results.Single(result => result.ProviderId == ProviderId.OpenCodeGo).State);
        Assert.Equal(
            ProviderDiscoveryState.RequiresSetup,
            results.Single(result => result.ProviderId == ProviderId.Zai).State);
    }

    private sealed class FakeExecutableLocator(IReadOnlyDictionary<string, string> executables)
        : IExecutableLocator
    {
        public string? FindExecutable(string executableName) =>
            executables.GetValueOrDefault(executableName);
    }
}
