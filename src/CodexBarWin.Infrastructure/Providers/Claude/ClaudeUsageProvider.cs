using System.Text;
using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Processes;

namespace CodexBarWin.Infrastructure.Providers.Claude;

public sealed class ClaudeUsageProvider(
    IPtySessionFactory ptySessionFactory,
    IExecutableLocator executableLocator,
    TimeProvider? timeProvider = null,
    ICliVersionReader? cliVersionReader = null) : IUsageProvider, ICliVersionProvider
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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
        string? executablePath = executableLocator.FindExecutable("claude");
        if (executablePath is null)
        {
            throw new ProviderException(ProviderErrorCategory.NotInstalled, "Claude Code is not installed or is not on PATH.");
        }

        string workingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexBarWin",
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

            DateTimeOffset settleUntil = this._timeProvider.GetUtcNow().AddSeconds(10);
            while (this._timeProvider.GetUtcNow() < settleUntil)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), this._timeProvider, timeout.Token).ConfigureAwait(false);
                string current;
                lock (captureLock)
                {
                    current = captured.ToString();
                }

                if (current.Contains("Current session", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1.25), this._timeProvider, timeout.Token).ConfigureAwait(false);
                    break;
                }

                if (current.Contains("Total cost:", StringComparison.OrdinalIgnoreCase)
                    || current.Contains("currently using your subscription", StringComparison.OrdinalIgnoreCase))
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
