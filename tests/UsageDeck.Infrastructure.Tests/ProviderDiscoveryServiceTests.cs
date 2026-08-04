using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Providers;
using UsageDeck.Infrastructure.Providers.TheClawBay;
using UsageDeck.Infrastructure.Security;
using UsageDeck.Infrastructure.Settings;

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

        Assert.Equal(ProviderId.Available, results.Select(result => result.ProviderId));
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

    [Theory]
    [InlineData(true, null, false)]
    [InlineData(false, "environment-key", false)]
    [InlineData(false, null, true)]
    public void DiscoverDetectsAnyConfiguredTheClawBaySource(
        bool cliInstalled,
        string? environmentKey,
        bool hasWindowsCredential)
    {
        ProviderDiscoveryService service = new(
            new FakeExecutableLocator(new Dictionary<string, string>()),
            () => null,
            () => environmentKey,
            () => hasWindowsCredential,
            new FixedTheClawBayCliCommandResolver(cliInstalled
                ? new TheClawBayCliCommand(@"C:\Tools\node.exe", [@"C:\Tools\node_modules\theclawbay\dist\index.js"])
                : null));

        ProviderDiscoveryResult result = service.Discover()
            .Single(value => value.ProviderId == ProviderId.TheClawBay);

        Assert.Equal(ProviderDiscoveryState.Detected, result.State);
        ApiKeyStorageMode? expectedStorage = environmentKey is not null
            ? ApiKeyStorageMode.EnvironmentVariable
            : hasWindowsCredential
                ? ApiKeyStorageMode.WindowsCredentialManager
                : null;
        Assert.Equal(expectedStorage, result.DetectedApiKeyStorage);
        Assert.DoesNotContain("environment-key", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Tools", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscoverRequiresTheClawBaySetupWhenNoSourceIsConfigured()
    {
        ProviderDiscoveryService service = new(
            new FakeExecutableLocator(new Dictionary<string, string>()),
            () => null,
            () => null,
            () => false);

        ProviderDiscoveryResult result = service.Discover()
            .Single(value => value.ProviderId == ProviderId.TheClawBay);

        Assert.Equal(ProviderDiscoveryState.RequiresSetup, result.State);
        Assert.Contains("theclawbay setup", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("add an API key in Settings", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscoverReportsTheClawBayCredentialProbeFailureWithoutTreatingItAsMissing()
    {
        ProviderDiscoveryService service = new(
            new FakeExecutableLocator(new Dictionary<string, string>()),
            () => null,
            () => null,
            () => throw new SecretStoreException(
                "Windows Credential Manager could not be read.",
                new InvalidOperationException(@"private-key C:\Sensitive\credential")),
            new FixedTheClawBayCliCommandResolver(null));

        ProviderDiscoveryResult result = service.Discover()
            .Single(value => value.ProviderId == ProviderId.TheClawBay);

        Assert.Equal(ProviderDiscoveryState.Unavailable, result.State);
        Assert.Contains("could not be read", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-key", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Sensitive", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscoverOmitsUnavailableOpenCodeWithoutProbingForIt()
    {
        bool openCodeDataProbed = false;
        ProviderDiscoveryService service = new(
            new FakeExecutableLocator(new Dictionary<string, string>()),
            () =>
            {
                openCodeDataProbed = true;
                return @"C:\Users\Test\opencode.db";
            });

        IReadOnlyList<ProviderDiscoveryResult> results = service.Discover();

        Assert.DoesNotContain(results, result => result.ProviderId == ProviderId.OpenCodeGo);
        Assert.False(openCodeDataProbed);
    }

    [Fact]
    public void DiscoverKeepsZaiAvailableForManualSetup()
    {
        ProviderDiscoveryService service = new(
            new FakeExecutableLocator(new Dictionary<string, string>()),
            () => null);

        IReadOnlyList<ProviderDiscoveryResult> results = service.Discover();

        Assert.Equal(
            ProviderDiscoveryState.RequiresSetup,
            results.Single(result => result.ProviderId == ProviderId.Zai).State);
    }

    [Fact]
    public void DiscoverStopsBeforeTheNextProviderWhenCancelled()
    {
        using CancellationTokenSource cancellation = new();
        List<string> probes = [];
        CallbackExecutableLocator locator = new(executableName =>
        {
            probes.Add(executableName);
            cancellation.Cancel();
            return null;
        });
        ProviderDiscoveryService service = new(locator, () => null);

        Assert.Throws<OperationCanceledException>(() => service.Discover(cancellation.Token));

        Assert.Equal(["codex"], probes);
    }

    [Fact]
    public void DiscoverStopsBeforeWindowsCredentialProbeWhenEnvironmentProbeCancels()
    {
        using CancellationTokenSource cancellation = new();
        bool windowsCredentialProbed = false;
        ProviderDiscoveryService service = new(
            new FakeExecutableLocator(new Dictionary<string, string>()),
            () => null,
            () =>
            {
                cancellation.Cancel();
                return null;
            },
            () =>
            {
                windowsCredentialProbed = true;
                return false;
            });

        Assert.Throws<OperationCanceledException>(() => service.Discover(cancellation.Token));

        Assert.False(windowsCredentialProbed);
    }

    private sealed class FakeExecutableLocator(IReadOnlyDictionary<string, string> executables)
        : IExecutableLocator
    {
        public string? FindExecutable(string executableName) =>
            executables.GetValueOrDefault(executableName);
    }

    private sealed class CallbackExecutableLocator(Func<string, string?> findExecutable)
        : IExecutableLocator
    {
        public string? FindExecutable(string executableName) => findExecutable(executableName);
    }

    private sealed class FixedTheClawBayCliCommandResolver(TheClawBayCliCommand? command)
        : ITheClawBayCliCommandResolver
    {
        public TheClawBayCliCommand? Resolve() => command;
    }
}
