using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Providers.OpenCodeGo;
using UsageDeck.Infrastructure.Providers.TheClawBay;
using UsageDeck.Infrastructure.Security;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.Infrastructure.Providers;

public enum ProviderDiscoveryState
{
    Detected,
    NotDetected,
    RequiresSetup,
    Unavailable,
}

public sealed record ProviderDiscoveryResult(
    ProviderId ProviderId,
    ProviderDiscoveryState State,
    string Detail,
    ApiKeyStorageMode? DetectedApiKeyStorage = null);

public sealed class ProviderDiscoveryService
{
    private static readonly IReadOnlyList<(ProviderId ProviderId, string ExecutableName, string SourceName)>
        CliSources =
        [
            (ProviderId.Codex, "codex", "Codex CLI"),
            (ProviderId.Claude, "claude", "Claude Code CLI"),
            (ProviderId.Antigravity, "agy", "Antigravity CLI"),
            (ProviderId.Copilot, "gh", "GitHub CLI"),
            (ProviderId.Kiro, "kiro-cli", "Kiro CLI"),
            (ProviderId.Amp, "amp", "Amp CLI"),
        ];

    private readonly IExecutableLocator _executableLocator;
    private readonly Func<string?> _openCodeDataPathReader;
    private readonly ITheClawBayCliCommandResolver _theClawBayCliCommandResolver;
    private readonly Func<string?> _theClawBayEnvironmentReader;
    private readonly Func<bool> _theClawBayWindowsCredentialPresence;

    public ProviderDiscoveryService(
        IExecutableLocator executableLocator,
        Func<string?>? openCodeDataPathReader = null,
        Func<string?>? theClawBayEnvironmentReader = null,
        Func<bool>? theClawBayWindowsCredentialPresence = null,
        ITheClawBayCliCommandResolver? theClawBayCliCommandResolver = null)
    {
        this._executableLocator = executableLocator
            ?? throw new ArgumentNullException(nameof(executableLocator));
        this._openCodeDataPathReader = openCodeDataPathReader
            ?? new OpenCodeGoDataLocator().FindDatabasePath;
        this._theClawBayCliCommandResolver = theClawBayCliCommandResolver
            ?? new TheClawBayCliCommandResolver(executableLocator);
        this._theClawBayEnvironmentReader = theClawBayEnvironmentReader
            ?? (() => Environment.GetEnvironmentVariable(TheClawBayApiKeyResolver.EnvironmentVariableName));
        this._theClawBayWindowsCredentialPresence = theClawBayWindowsCredentialPresence
            ?? (() => false);
    }

    public IReadOnlyList<ProviderDiscoveryResult> Discover(
        CancellationToken cancellationToken = default)
    {
        Dictionary<ProviderId, ProviderDiscoveryResult> results = [];
        foreach ((ProviderId providerId, string executableName, string sourceName) in CliSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderDiscoveryResult result = this.DiscoverCli(providerId, executableName, sourceName);
            results.Add(result.ProviderId, result);
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool hasTheClawBayCli = this._theClawBayCliCommandResolver.Resolve() is not null;
        cancellationToken.ThrowIfCancellationRequested();
        bool hasTheClawBayEnvironmentKey =
            !string.IsNullOrWhiteSpace(this._theClawBayEnvironmentReader());
        cancellationToken.ThrowIfCancellationRequested();
        bool hasTheClawBayWindowsCredential = false;
        bool theClawBayCredentialProbeFailed = false;
        if (!hasTheClawBayCli && !hasTheClawBayEnvironmentKey)
        {
            try
            {
                hasTheClawBayWindowsCredential = this._theClawBayWindowsCredentialPresence();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (SecretStoreException)
            {
                theClawBayCredentialProbeFailed = true;
            }
        }

        results[ProviderId.TheClawBay] = theClawBayCredentialProbeFailed
            ? new ProviderDiscoveryResult(
                ProviderId.TheClawBay,
                ProviderDiscoveryState.Unavailable,
                "Windows Credential Manager could not be read. Choose TheClawBay manually and review Settings.")
            : hasTheClawBayCli || hasTheClawBayEnvironmentKey || hasTheClawBayWindowsCredential
                ? new ProviderDiscoveryResult(
                    ProviderId.TheClawBay,
                    ProviderDiscoveryState.Detected,
                    "TheClawBay CLI or API-key configuration was found on this PC.",
                    hasTheClawBayEnvironmentKey
                        ? ApiKeyStorageMode.EnvironmentVariable
                        : hasTheClawBayWindowsCredential
                            ? ApiKeyStorageMode.WindowsCredentialManager
                            : null)
                : new ProviderDiscoveryResult(
                    ProviderId.TheClawBay,
                    ProviderDiscoveryState.RequiresSetup,
                    "Run theclawbay setup or add an API key in Settings after setup.");

        if (ProviderId.Available.Contains(ProviderId.OpenCodeGo))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool openCodeDetected = this._executableLocator.FindExecutable("opencode") is not null;
            cancellationToken.ThrowIfCancellationRequested();
            if (!openCodeDetected)
            {
                openCodeDetected = this._openCodeDataPathReader() is not null;
                cancellationToken.ThrowIfCancellationRequested();
            }

            results[ProviderId.OpenCodeGo] = openCodeDetected
                ? new ProviderDiscoveryResult(
                    ProviderId.OpenCodeGo,
                    ProviderDiscoveryState.Detected,
                    "OpenCode Go or its local usage history was found.")
                : new ProviderDiscoveryResult(
                    ProviderId.OpenCodeGo,
                    ProviderDiscoveryState.RequiresSetup,
                    "Use OpenCode Go once or add a Console service-account key in Settings.");
        }
        results[ProviderId.Zai] = new ProviderDiscoveryResult(
            ProviderId.Zai,
            ProviderDiscoveryState.RequiresSetup,
            "Add a Z.AI API key in Settings after setup.");

        cancellationToken.ThrowIfCancellationRequested();
        return ProviderId.Available.Select(providerId => results[providerId]).ToArray();
    }

    private ProviderDiscoveryResult DiscoverCli(
        ProviderId providerId,
        string executableName,
        string sourceName)
    {
        bool detected = this._executableLocator.FindExecutable(executableName) is not null;
        return new ProviderDiscoveryResult(
            providerId,
            detected ? ProviderDiscoveryState.Detected : ProviderDiscoveryState.NotDetected,
            detected
                ? $"{sourceName} was found on this PC."
                : $"{sourceName} was not found on this PC.");
    }
}
