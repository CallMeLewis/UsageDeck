using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Compatibility;
using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Providers.Antigravity;

public sealed class AntigravityUsageProvider(
    IPtySessionFactory ptySessionFactory,
    IExecutableLocator executableLocator,
    TimeProvider? timeProvider = null,
    ICliVersionReader? cliVersionReader = null) : IUsageProvider, ICliVersionProvider
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ProviderId Id => ProviderId.Antigravity;

    public string DisplayName => "Antigravity";

    public async Task<string?> ReadCliVersionAsync(CancellationToken cancellationToken)
    {
        string? executablePath = executableLocator.FindExecutable("agy");
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
        string? executablePath = executableLocator.FindExecutable("agy");
        if (executablePath is null)
        {
            throw new ProviderException(
                ProviderErrorCategory.NotInstalled,
                "Antigravity CLI is not installed or `agy` is not on PATH.");
        }

        string workingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyInstallIdentity.LocalDataDirectoryName,
            "AntigravityProbe");
        Directory.CreateDirectory(workingDirectory);

        PtyStartSpec spec = new(
            executablePath,
            [],
            workingDirectory,
            new Dictionary<string, string>
            {
                ["NO_COLOR"] = "1",
                ["TERM"] = "xterm-256color",
            },
            Columns: 140,
            Rows: 50);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(22));

        try
        {
            await using IPtySession session = await ptySessionFactory.StartAsync(spec, timeout.Token).ConfigureAwait(false);
            StringBuilder captured = new(capacity: 32_768);
            object captureLock = new();
            Task captureTask = CaptureAsync(session, captured, captureLock, timeout.Token);

            await Task.Delay(TimeSpan.FromSeconds(3), this._timeProvider, timeout.Token).ConfigureAwait(false);
            await session.WriteAsync(Encoding.UTF8.GetBytes("/usage"), timeout.Token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(150), this._timeProvider, timeout.Token).ConfigureAwait(false);
            await session.WriteAsync("\r"u8.ToArray(), timeout.Token).ConfigureAwait(false);

            DateTimeOffset settleUntil = this._timeProvider.GetUtcNow().AddSeconds(14);
            while (this._timeProvider.GetUtcNow() < settleUntil)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), this._timeProvider, timeout.Token).ConfigureAwait(false);
                string current;
                lock (captureLock)
                {
                    current = captured.ToString();
                }

                if (HasQuotaPanel(current))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1.25), this._timeProvider, timeout.Token).ConfigureAwait(false);
                    break;
                }

                if (current.Contains("sign in", StringComparison.OrdinalIgnoreCase)
                    || current.Contains("log in", StringComparison.OrdinalIgnoreCase))
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
            IReadOnlyList<UsageWindow> windows = AntigravityUsageParser.Parse(output, capturedAt);
            return new ProviderSnapshot(
                this.Id,
                this.DisplayName,
                "Antigravity CLI",
                capturedAt,
                UsageDataState.Fresh,
                windows);
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Transient,
                "Antigravity did not return quota data in time.",
                exception);
        }
        catch (Exception exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                "Antigravity usage could not be read.",
                exception);
        }
    }

    private static bool HasQuotaPanel(string output) =>
        output.Contains('%', StringComparison.Ordinal)
        && (output.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || output.Contains("remaining", StringComparison.OrdinalIgnoreCase)
            || output.Contains("used", StringComparison.OrdinalIgnoreCase));

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
