using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Compatibility;
using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Providers.Claude;

public sealed partial class ClaudeUsageProvider(
    IPtySessionFactory ptySessionFactory,
    IExecutableLocator executableLocator,
    TimeProvider? timeProvider = null,
    ICliVersionReader? cliVersionReader = null,
    HttpClient? httpClient = null,
    IClaudeCredentialsReader? credentialsReader = null,
    Func<bool>? useUsageApi = null,
    IBoundedProcessRunner? processRunner = null) : IUsageProvider, ICliVersionProvider
{
    // The first release whose print mode answers /usage with the plan limits. Releases from
    // 2.1.118 print only the session cost, and earlier ones send /usage to the model as a prompt,
    // which would spend the very quota UsageDeck is reporting.
    private static readonly Version MinimumPrintedUsageVersion = new(2, 1, 178);
    private static readonly TimeSpan PrintedUsageTimeout = TimeSpan.FromSeconds(30);
    private const int MaximumPrintedUsageBytes = 262_144;
    private const int MaximumPrintedUsageErrorBytes = 16_384;

    // The same query Claude Code sends when it checks for limit resets. Without it the endpoint
    // leaves the resets block empty; skip_spend drops spend figures UsageDeck does not read.
    private static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage?cedar_ember=1&skip_spend=1");
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PromptBudget = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PromptQuietPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CommandEchoBudget = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CommandEchoPollInterval = TimeSpan.FromMilliseconds(100);
    private const string PromptRule = "────";
    private static readonly TimeSpan TrustPromptRedrawDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SettlePollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1.5);
    private const int MaximumApiResponseBytes = 1_048_576;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IClaudeCredentialsReader _credentialsReader = credentialsReader ?? new ClaudeCredentialsReader();
    private readonly Func<bool> _useUsageApi = useUsageApi ?? (() => false);

    public ProviderId Id => ProviderId.Claude;

    public string DisplayName => "Claude";

    public async Task<string?> ReadCliVersionAsync(CancellationToken cancellationToken)
    {
        string? executablePath = executableLocator.FindExecutable("claude");
        if (executablePath is null || cliVersionReader is null)
        {
            return null;
        }

        return await cliVersionReader.ReadAsync(
            new ProcessStartSpec(executablePath, ["--version"]),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        // Reading the /usage panel through the unmodified CLI is the default because Anthropic
        // intends Claude Code's sign-in for Claude Code itself. The panel is drawn from an API the
        // CLI calls, so people who opt in can ask that API directly with the CLI's own token:
        // sub-second instead of booting a whole Claude Code session, and immune to the panel's
        // rendering quirks. Any failure - no credentials, expired token, endpoint changed - falls
        // back to scraping the CLI, which also refreshes the CLI's token for the next attempt.
        // UsageDeck never writes to Claude's credential store itself.
        AccountIdentity? identity = null;
        if (httpClient is not null)
        {
            ClaudeCredentials? credentials = this._credentialsReader.Read();
            identity = credentials?.Plan is null ? null : new AccountIdentity(null, credentials.Plan);
            if (this._useUsageApi()
                && credentials is not null
                && credentials.ExpiresAt > this._timeProvider.GetUtcNow().AddMinutes(1))
            {
                ProviderSnapshot? snapshot = await this.TryFetchFromApiAsync(
                    credentials.AccessToken, identity, cancellationToken).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    return snapshot;
                }
            }
        }

        return await this.FetchFromCliAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderSnapshot?> TryFetchFromApiAsync(
        string accessToken,
        AccountIdentity? identity,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApiTimeout);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, UsageEndpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

            // Anthropic only reports limit resets to a current Claude Code CLI, so the request
            // carries the installed CLI's own identifier. Usage limits are returned either way.
            string? cliVersion = await this.ReadCliVersionAsync(timeout.Token).ConfigureAwait(false);
            if (cliVersion is not null)
            {
                request.Headers.TryAddWithoutValidation("User-Agent", $"claude-cli/{cliVersion} (external, cli)");
            }

            using HttpResponseMessage response = await httpClient!.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            UsageRetryBackoff.ThrowIfRequested(response, this.DisplayName, this._timeProvider.GetUtcNow());
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            byte[] body = await ReadBoundedApiResponseAsync(response.Content, timeout.Token).ConfigureAwait(false);
            string json = Encoding.UTF8.GetString(body);
            IReadOnlyList<UsageWindow> windows = ClaudeApiUsageParser.Parse(json);
            return new ProviderSnapshot(
                this.Id,
                this.DisplayName,
                "Claude API",
                this._timeProvider.GetUtcNow(),
                UsageDataState.Fresh,
                windows,
                identity,
                resetCredits: ClaudeApiUsageParser.ParseResetCredits(json));
        }
        catch (ProviderException exception) when (exception.RetryNotBeforeUtc is not null)
        {
            // Falling back to the CLI would immediately query the same throttled usage service.
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ProviderException or HttpRequestException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedApiResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        long? length = content.Headers.ContentLength;
        if (length > MaximumApiResponseBytes)
        {
            throw ApiResponseTooLarge();
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream body = new(length is > 0 ? checked((int)length.Value) : 4096);
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return body.ToArray();
            }

            if (body.Length + read > MaximumApiResponseBytes)
            {
                throw ApiResponseTooLarge();
            }

            body.Write(buffer, 0, read);
        }
    }

    private static ProviderException ApiResponseTooLarge() => new(
        ProviderErrorCategory.InvalidResponse,
        "Claude returned a usage response that was too large to process safely.");

    private async Task<ProviderSnapshot> FetchFromCliAsync(
        AccountIdentity? identity,
        CancellationToken cancellationToken)
    {
        string? executablePath = executableLocator.FindExecutable("claude");
        if (executablePath is null)
        {
            throw new ProviderException(ProviderErrorCategory.NotInstalled, "Claude Code is not installed or is not on PATH.");
        }

        string workingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationIdentity.LocalDataDirectoryName,
            "ClaudeProbe");
        Directory.CreateDirectory(workingDirectory);

        if (processRunner is not null
            && SupportsPrintedUsage(await this.ReadCliVersionAsync(cancellationToken).ConfigureAwait(false)))
        {
            return await this.FetchPrintedUsageAsync(
                processRunner, executablePath, workingDirectory, identity, cancellationToken).ConfigureAwait(false);
        }

        PtyStartSpec spec = new(
            executablePath,
            ["--allowedTools", "", "--permission-mode", "plan"],
            workingDirectory,
            new Dictionary<string, string>
            {
                ["CLAUDE_CODE_DISABLE_TERMINAL_TITLE"] = "1",
                ["DISABLE_AUTOUPDATER"] = "1",
            },
            ClaudeUsageParser.ScreenColumns,
            ClaudeUsageParser.ScreenRows);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));

        try
        {
            await using IPtySession session = await ptySessionFactory.StartAsync(spec, timeout.Token).ConfigureAwait(false);
            StringBuilder captured = new(capacity: 32_768);
            object captureLock = new();
            Task captureTask = CaptureAsync(session, captured, captureLock, timeout.Token);

            await this.WaitForPromptAsync(captured, captureLock, 0, timeout.Token).ConfigureAwait(false);
            await this.AcceptWorkspaceTrustAsync(
                session, captured, captureLock, workingDirectory, timeout.Token).ConfigureAwait(false);
            await this.SubmitUsageCommandAsync(session, captured, captureLock, timeout.Token).ConfigureAwait(false);

            // The panel does not paint atomically. Measured captures show the per-model weekly
            // row landing 200-550ms after the rest of the panel on an idle machine, and later
            // when it is busy, so waiting a fixed time after the first row appears truncates the
            // capture under load. Wait for the output to stop growing instead: the CLI goes quiet
            // once the panel is complete, so this settles sooner than a fixed wait in the common
            // case and still tolerates a slow final row.
            DateTimeOffset settleUntil = this._timeProvider.GetUtcNow().Add(SettleBudget);
            int lastLength = 0;
            DateTimeOffset? quietSince = null;
            while (this._timeProvider.GetUtcNow() < settleUntil)
            {
                await Task.Delay(SettlePollInterval, this._timeProvider, timeout.Token).ConfigureAwait(false);
                string current;
                lock (captureLock)
                {
                    current = captured.ToString();
                }

                if (current.Length != lastLength)
                {
                    lastLength = current.Length;
                    quietSince = null;
                }
                else
                {
                    quietSince ??= this._timeProvider.GetUtcNow();
                }

                // Match against a compacted copy: some frames are laid out with cursor movement
                // rather than literal spaces, so the markers arrive without any whitespace.
                string markers = ClaudeUsageParser.Compact(
                    ClaudeUsageParser.StripTerminalSequences(current));

                // "Total cost:" is not a reason to stop straight away: subscription panels open with
                // a session cost section too, and the limits, above all the per-model weekly row,
                // are painted a moment after it.
                bool hasPanel = markers.Contains("currentsession", StringComparison.OrdinalIgnoreCase)
                    || markers.Contains("totalcost:", StringComparison.OrdinalIgnoreCase)
                    || markers.Contains("currentlyusingyoursubscription", StringComparison.OrdinalIgnoreCase);
                if (hasPanel
                    && quietSince is not null
                    && this._timeProvider.GetUtcNow() - quietSince.Value >= QuietPeriod)
                {
                    break;
                }
            }

            session.Kill();
            timeout.Cancel();
            await IgnoreCancellationAsync(captureTask).ConfigureAwait(false);

            string output;
            lock (captureLock)
            {
                output = captured.ToString();
            }

            DateTimeOffset capturedAt = this._timeProvider.GetUtcNow();
            IReadOnlyList<UsageWindow> windows = ClaudeUsageParser.Parse(output, capturedAt);
            return new ProviderSnapshot(
                this.Id,
                this.DisplayName,
                "Claude CLI",
                capturedAt,
                UsageDataState.Fresh,
                windows,
                identity);
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(ProviderErrorCategory.Transient, "Claude did not return usage data in time.", exception);
        }
        catch (Exception exception)
        {
            throw new ProviderException(ProviderErrorCategory.Unavailable, "Claude usage could not be read.", exception);
        }
    }

    internal static bool SupportsPrintedUsage(string? cliVersion)
    {
        string? release = cliVersion?.Split('-', '+')[0];
        return Version.TryParse(release, out Version? version) && version >= MinimumPrintedUsageVersion;
    }

    /// <summary>
    /// Runs /usage once in print mode. Claude Code answers it locally, without a model request,
    /// and prints the limits as plain lines once they have loaded, so there is no terminal to
    /// drive and no repaint to wait out. MCP servers and tools are left off because the command
    /// needs neither, and session persistence is off so refreshes do not fill Claude Code's
    /// history.
    /// </summary>
    private async Task<ProviderSnapshot> FetchPrintedUsageAsync(
        IBoundedProcessRunner runner,
        string executablePath,
        string workingDirectory,
        AccountIdentity? identity,
        CancellationToken cancellationToken)
    {
        ProcessStartSpec spec = new(
            executablePath,
            ["-p", "/usage", "--no-session-persistence", "--strict-mcp-config", "--tools", ""],
            workingDirectory,
            new Dictionary<string, string?> { ["DISABLE_AUTOUPDATER"] = "1" });

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PrintedUsageTimeout);

        ProcessRunResult result;
        try
        {
            result = await runner.RunAsync(
                spec, MaximumPrintedUsageBytes, MaximumPrintedUsageErrorBytes, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(ProviderErrorCategory.Transient, "Claude did not return usage data in time.", exception);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception)
        {
            throw new ProviderException(ProviderErrorCategory.Unavailable, "Claude usage could not be read.", exception);
        }

        // Standard error is not surfaced: Claude Code may echo account details there.
        string output = Encoding.UTF8.GetString(result.StandardOutput);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                $"Claude Code could not show usage (exit code {result.ExitCode}).");
        }

        DateTimeOffset capturedAt = this._timeProvider.GetUtcNow();
        return new ProviderSnapshot(
            this.Id,
            this.DisplayName,
            "Claude CLI",
            capturedAt,
            UsageDataState.Fresh,
            ClaudeUsageParser.ParsePrinted(output, capturedAt),
            identity);
    }

    /// <summary>
    /// Claude Code asks whether to trust a folder the first time it opens there, with "No, exit"
    /// preselected, so sending /usage and Enter would quit the session. UsageDeck answers only for
    /// its own empty probe folder, and only confirms once the trust row is the selected one.
    /// Claude Code records the answer itself, so the prompt appears once.
    /// </summary>
    private async Task AcceptWorkspaceTrustAsync(
        IPtySession session,
        StringBuilder captured,
        object captureLock,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        string prompt = ReadCompactScreen(captured, captureLock, 0);
        if (!prompt.Contains("trustthisfolder", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Directory.EnumerateFileSystemEntries(workingDirectory).Any())
        {
            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                "Claude Code asked whether to trust UsageDeck's folder, but that folder is not empty. "
                + @"Empty %LOCALAPPDATA%\UsageDeck\ClaudeProbe and refresh.");
        }

        if (!TrustRowSelectedRegex().IsMatch(prompt))
        {
            if (!TrustRowBelowSelectionRegex().IsMatch(prompt))
            {
                throw TrustPromptNotAnswered();
            }

            int redrawStart;
            lock (captureLock)
            {
                redrawStart = captured.Length;
            }

            // Claude Code repaints only the cells that changed, so moving down one row shows up
            // as the selector on its own rather than as the trust row being printed again.
            await session.WriteAsync("\u001b[B"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await Task.Delay(TrustPromptRedrawDelay, this._timeProvider, cancellationToken).ConfigureAwait(false);
            if (!SelectorOnlyRegex().IsMatch(ReadCompactScreen(captured, captureLock, redrawStart)))
            {
                throw TrustPromptNotAnswered();
            }
        }

        int promptStart;
        lock (captureLock)
        {
            promptStart = captured.Length;
        }

        await session.WriteAsync("\r"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await this.WaitForPromptAsync(captured, captureLock, promptStart, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Claude Code paints its prompt asynchronously and takes longer when the machine is busy, as
    /// it is when every provider refreshes at once. Output going quiet is not enough on its own
    /// because start-up pauses for over a second before the prompt exists, and anything typed
    /// into that gap is held back and replayed later. Waits for a ruled box, which both the prompt
    /// and the trust question draw, to appear after <paramref name="start"/> and stop changing.
    /// Running out of budget is not an error because <see cref="SubmitUsageCommandAsync"/> never
    /// presses Enter on input it has not seen echoed.
    /// </summary>
    private async Task WaitForPromptAsync(
        StringBuilder captured,
        object captureLock,
        int start,
        CancellationToken cancellationToken)
    {
        DateTimeOffset waitUntil = this._timeProvider.GetUtcNow().Add(PromptBudget);
        int lastLength = start;
        DateTimeOffset? quietSince = null;
        while (this._timeProvider.GetUtcNow() < waitUntil)
        {
            await Task.Delay(SettlePollInterval, this._timeProvider, cancellationToken).ConfigureAwait(false);
            int length;
            lock (captureLock)
            {
                length = captured.Length;
            }

            if (length != lastLength)
            {
                lastLength = length;
                quietSince = null;
                continue;
            }

            if (!ReadCompactScreen(captured, captureLock, start).Contains(PromptRule, StringComparison.Ordinal))
            {
                continue;
            }

            quietSince ??= this._timeProvider.GetUtcNow();
            if (this._timeProvider.GetUtcNow() - quietSince.Value >= PromptQuietPeriod)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Types /usage exactly once and presses Enter only after Claude Code has echoed it back.
    /// Input is never retyped: early keystrokes are replayed rather than lost, so typing again can
    /// leave "/usage/usage" in the box, which Claude Code would send to the model as a prompt.
    /// </summary>
    private async Task SubmitUsageCommandAsync(
        IPtySession session,
        StringBuilder captured,
        object captureLock,
        CancellationToken cancellationToken)
    {
        int echoStart;
        lock (captureLock)
        {
            echoStart = captured.Length;
        }

        await session.WriteAsync(Encoding.UTF8.GetBytes("/usage"), cancellationToken).ConfigureAwait(false);
        DateTimeOffset echoUntil = this._timeProvider.GetUtcNow().Add(CommandEchoBudget);
        while (this._timeProvider.GetUtcNow() < echoUntil)
        {
            await Task.Delay(CommandEchoPollInterval, this._timeProvider, cancellationToken).ConfigureAwait(false);
            if (ReadCompactScreen(captured, captureLock, echoStart)
                .Contains("/usage", StringComparison.OrdinalIgnoreCase))
            {
                await session.WriteAsync("\r"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        throw new ProviderException(
            ProviderErrorCategory.Transient,
            "Claude Code started but did not accept the usage command in time.");
    }

    private static ProviderException TrustPromptNotAnswered() => new(
        ProviderErrorCategory.Unavailable,
        "Claude Code asked whether to trust UsageDeck's folder and UsageDeck could not answer it. "
        + @"Run `claude` once in %LOCALAPPDATA%\UsageDeck\ClaudeProbe, trust the folder, and refresh.");

    private static string ReadCompactScreen(StringBuilder captured, object captureLock, int start)
    {
        string text;
        lock (captureLock)
        {
            text = captured.ToString(start, captured.Length - start);
        }

        return ClaudeUsageParser.Compact(ClaudeUsageParser.StripTerminalSequences(text));
    }

    [GeneratedRegex("[>❯]yes,itrustthisfolder", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrustRowSelectedRegex();

    [GeneratedRegex("[>❯]no,exityes,itrustthisfolder", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrustRowBelowSelectionRegex();

    [GeneratedRegex("^[>❯]$")]
    private static partial Regex SelectorOnlyRegex();

    private static async Task CaptureAsync(
        IPtySession session,
        StringBuilder captured,
        object captureLock,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (captured.Length < 262_144)
            {
                int read = await session.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                lock (captureLock)
                {
                    captured.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
