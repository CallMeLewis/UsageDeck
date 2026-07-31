using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Providers;
using UsageDeck.Infrastructure.Providers.TheClawBay;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.Infrastructure.Tests;

public sealed class TheClawBayCliCommandResolverTests : IDisposable
{
    private const string QuotaJson = """
        {
          "observedAt": "2026-07-31T16:00:00Z",
          "usage": {
            "fiveHour": { "percentUsed": 27.5, "windowEnd": "2026-07-31T20:00:00Z" },
            "weekly": { "percentUsed": 63, "windowEnd": "2026-08-03T00:00:00Z" }
          }
        }
        """;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "UsageDeck.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResolveUsesNodeAndTheFixedPackageEntryForAWindowsNpmInstallation()
    {
        (ExecutableLocator locator, string nodePath, string packageEntryPath) =
            this.CreateWindowsNpmLayout();

        TheClawBayCliCommand? command = new TheClawBayCliCommandResolver(locator).Resolve();

        Assert.NotNull(command);
        Assert.Equal(nodePath, command.ExecutablePath);
        Assert.Equal([packageEntryPath], command.PrefixArguments);
        Assert.DoesNotContain(".cmd", command.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscoveryDetectsTheFixedPackageEntryFromAWindowsNpmInstallation()
    {
        (ExecutableLocator locator, _, _) = this.CreateWindowsNpmLayout();
        TheClawBayCliCommandResolver resolver = new(locator);
        ProviderDiscoveryService service = new(
            locator,
            openCodeDataPathReader: () => null,
            theClawBayEnvironmentReader: () => null,
            theClawBayWindowsCredentialPresence: () => false,
            theClawBayCliCommandResolver: resolver);

        ProviderDiscoveryResult result = service.Discover()
            .Single(value => value.ProviderId == ProviderId.TheClawBay);

        Assert.Equal(ProviderDiscoveryState.Detected, result.State);
        Assert.DoesNotContain(this._directory, result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VersionInvocationUsesNodeAndTheFixedWindowsNpmPackageEntry()
    {
        (ExecutableLocator locator, string nodePath, string packageEntryPath) =
            this.CreateWindowsNpmLayout();
        TheClawBayCliCommandResolver resolver = new(locator);
        RecordingCliVersionReader versionReader = new("0.6.13");
        TheClawBayUsageProvider provider = new(
            new RecordingProcessRunner(new ProcessRunResult([], 0, string.Empty)),
            resolver,
            new HttpClient(new ThrowingHttpHandler()),
            new EmptyApiKeySource(),
            () => TheClawBayUsageSource.Automatic,
            versionReader);

        string? version = await provider.ReadCliVersionAsync(CancellationToken.None);

        Assert.Equal("0.6.13", version);
        Assert.Equal(nodePath, versionReader.Spec?.ExecutablePath);
        Assert.Equal([packageEntryPath, "--version"], versionReader.Spec?.Arguments);
    }

    [Fact]
    public async Task UsageInvocationUsesNodeAndTheFixedWindowsNpmPackageEntry()
    {
        (ExecutableLocator locator, string nodePath, string packageEntryPath) =
            this.CreateWindowsNpmLayout();
        TheClawBayCliCommandResolver resolver = new(locator);
        RecordingProcessRunner processes = new(new ProcessRunResult(
            Encoding.UTF8.GetBytes(QuotaJson),
            0,
            string.Empty));
        TheClawBayUsageProvider provider = new(
            processes,
            resolver,
            new HttpClient(new ThrowingHttpHandler()),
            new EmptyApiKeySource(),
            () => TheClawBayUsageSource.Cli);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("TheClawBay CLI", snapshot.SourceDescription);
        Assert.Equal(nodePath, processes.StartSpec?.ExecutablePath);
        Assert.Equal([packageEntryPath, "usage", "--json"], processes.StartSpec?.Arguments);
        Assert.Equal("1", processes.StartSpec?.Environment?["NO_COLOR"]);
        Assert.Equal("dumb", processes.StartSpec?.Environment?["TERM"]);
    }

    [Fact]
    public void ResolvePreservesADirectNativeExecutable()
    {
        string nativeDirectory = Path.Combine(this._directory, "native");
        string executablePath = this.CreateFile(Path.Combine("native", "theclawbay.exe"));
        TheClawBayCliCommandResolver resolver = new(new ExecutableLocator(nativeDirectory));

        TheClawBayCliCommand? command = resolver.Resolve();

        Assert.NotNull(command);
        Assert.Equal(executablePath, command.ExecutablePath);
        Assert.Empty(command.PrefixArguments);
    }

    public void Dispose()
    {
        if (Directory.Exists(this._directory))
        {
            Directory.Delete(this._directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string CreateFile(string relativePath, string contents = "")
    {
        string path = Path.Combine(this._directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private (ExecutableLocator Locator, string NodePath, string PackageEntryPath)
        CreateWindowsNpmLayout()
    {
        string npmDirectory = Path.Combine(this._directory, "npm");
        string nodeDirectory = Path.Combine(this._directory, "node-bin");
        string nodePath = this.CreateFile(Path.Combine("node-bin", "node.exe"));
        _ = this.CreateFile(
            Path.Combine("npm", "theclawbay.cmd"),
            "untrusted shim contents must not be executed");
        string packageEntryPath = this.CreateFile(
            Path.Combine("npm", "node_modules", "theclawbay", "dist", "index.js"));
        string path = string.Join(Path.PathSeparator, npmDirectory, nodeDirectory);
        return (new ExecutableLocator(path), nodePath, packageEntryPath);
    }

    private sealed class EmptyApiKeySource : ITheClawBayApiKeySource
    {
        public string? ReadApiKey() => null;
    }

    private sealed class RecordingCliVersionReader(string? version) : ICliVersionReader
    {
        public ProcessStartSpec? Spec { get; private set; }

        public Task<string?> ReadAsync(ProcessStartSpec spec, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Spec = spec;
            return Task.FromResult(version);
        }
    }

    private sealed class RecordingProcessRunner(ProcessRunResult result) : IBoundedProcessRunner
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
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP should not be called by the CLI integration test.");
    }
}
