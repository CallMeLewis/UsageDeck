using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Compatibility;
using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Providers.Claude;

public sealed class ClaudeUsageProvider(
    IPtySessionFactory ptySessionFactory,
    IExecutableLocator executableLocator,
    TimeProvider? timeProvider = null,
    ICliVersionReader? cliVersionReader = null,
    HttpClient? httpClient = null,
    IClaudeCredentialsReader? credentialsReader = null) : IUsageProvider, ICliVersionProvider
{
    private static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SettlePollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1.5);
    private const int MaximumApiResponseBytes = 1_048_576;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IClaudeCredentialsReader _credentialsReader = credentialsReader ?? new ClaudeCredentialsReader();

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
        // The /usage panel is drawn from an API the CLI calls, so ask that API directly with the
        // CLI's own token: sub-second instead of booting a whole Claude Code session, and immune
        // to the panel's rendering quirks. Any failure - no credentials, expired token, endpoint
        // changed - falls back to scraping the CLI, which also refreshes the CLI's token for the
        // next attempt. UsageDeck never writes to Claude's credential store itself.
        if (httpClient is not null)
        {
            ClaudeCredentials? credentials = this._credentialsReader.Read();
            if (credentials is not null && credentials.ExpiresAt > this._timeProvider.GetUtcNow().AddMinutes(1))
            {
                ProviderSnapshot? snapshot = await this.TryFetchFromApiAsync(
                    credentials.AccessToken, cancellationToken).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    return snapshot;
                }
            }
        }

        return await this.FetchFromCliAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderSnapshot?> TryFetchFromApiAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, UsageEndpoint);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApiTimeout);

        try
        {
            using HttpResponseMessage response = await httpClient!.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
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
                windows);
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

    private async Task<ProviderSnapshot> FetchFromCliAsync(CancellationToken cancellationToken)
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

        PtyStartSpec spec = new(
            executablePath,
            ["--allowedTools", "", "--permission-mode", "plan"],
            workingDirectory,
            new Dictionary<string, string>
            {
                ["CLAUDE_CODE_DISABLE_TERMINAL_TITLE"] = "1",
                ["DISABLE_AUTOUPDATER"] = "1",
            });

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(18));

        try
        {
            await using IPtySession session = await ptySessionFactory.StartAsync(spec, timeout.Token).ConfigureAwait(false);
            StringBuilder captured = new(capacity: 32_768);
            object captureLock = new();
            Task captureTask = CaptureAsync(session, captured, captureLock, timeout.Token);

            await Task.Delay(TimeSpan.FromSeconds(4), this._timeProvider, timeout.Token).ConfigureAwait(false);
            await session.WriteAsync(Encoding.UTF8.GetBytes("/usage"), timeout.Token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(150), this._timeProvider, timeout.Token).ConfigureAwait(false);
            await session.WriteAsync("\r"u8.ToArray(), timeout.Token).ConfigureAwait(false);

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

                if (markers.Contains("totalcost:", StringComparison.OrdinalIgnoreCase)
                    || markers.Contains("currentlyusingyoursubscription", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (quietSince is not null
                    && this._timeProvider.GetUtcNow() - quietSince.Value >= QuietPeriod
                    && markers.Contains("currentsession", StringComparison.OrdinalIgnoreCase))
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
                windows);
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
