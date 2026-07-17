using System.Text;
using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Processes;

namespace CodexBarWin.Infrastructure.Providers.Kiro;

public sealed class KiroUsageProvider(
    IProcessSessionFactory processSessionFactory,
    IPtySessionFactory ptySessionFactory,
    IExecutableLocator executableLocator,
    TimeProvider? timeProvider = null,
    ICliVersionReader? cliVersionReader = null) : IUsageProvider, ICliVersionProvider
{
    private const int MaximumResponseLength = 262_144;
    private static readonly string[] Arguments = ["chat", "--no-interactive", "/usage"];
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ProviderId Id => ProviderId.Kiro;

    public string DisplayName => ProviderId.Kiro.DisplayName;

    public async Task<string?> ReadCliVersionAsync(CancellationToken cancellationToken)
    {
        string? executablePath = executableLocator.FindExecutable("kiro-cli");
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
        string? executablePath = executableLocator.FindExecutable("kiro-cli");
        if (executablePath is null)
        {
            throw new ProviderException(
                ProviderErrorCategory.NotInstalled,
                "Kiro CLI is not installed or `kiro-cli` is not on PATH.");
        }

        string workingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexBarWin",
            "KiroProbe");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            return await this.FetchViaPipeAsync(executablePath, workingDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProviderException exception) when (exception.Category == ProviderErrorCategory.AuthenticationRequired)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Older Kiro releases may emit usable output only when attached to a terminal.
        }

        return await this.FetchViaPtyAsync(executablePath, workingDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ProviderSnapshot> FetchViaPipeAsync(
        string executablePath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessStartSpec spec = new(
            executablePath,
            Arguments,
            workingDirectory,
            new Dictionary<string, string?>
            {
                ["NO_COLOR"] = "1",
                ["TERM"] = "dumb",
            });

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        await using IProcessSession session = processSessionFactory.Start(spec);
        StringBuilder output = new(capacity: 16_384);
        while (await session.ReadLineAsync(timeout.Token).ConfigureAwait(false) is string line)
        {
            AppendBounded(output, line + Environment.NewLine);
        }

        return KiroUsageParser.Parse(output.ToString(), this._timeProvider.GetUtcNow());
    }

    private async Task<ProviderSnapshot> FetchViaPtyAsync(
        string executablePath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        PtyStartSpec spec = new(
            executablePath,
            Arguments,
            workingDirectory,
            new Dictionary<string, string>
            {
                ["NO_COLOR"] = "1",
                ["TERM"] = "xterm-256color",
            },
            Columns: 140,
            Rows: 40);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            await using IPtySession session = await ptySessionFactory.StartAsync(spec, timeout.Token).ConfigureAwait(false);
            StringBuilder captured = new(capacity: 16_384);
            object captureLock = new();
            Task captureTask = CaptureAsync(session, captured, captureLock, timeout.Token);

            while (!captureTask.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), this._timeProvider, timeout.Token).ConfigureAwait(false);
                string current;
                lock (captureLock)
                {
                    current = captured.ToString();
                }

                if (CanParse(current))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), this._timeProvider, timeout.Token).ConfigureAwait(false);
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

            return KiroUsageParser.Parse(output, this._timeProvider.GetUtcNow());
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
                "Kiro did not return usage details in time.",
                exception);
        }
        catch (Exception exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                "Kiro usage could not be read.",
                exception);
        }
    }

    private static bool CanParse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        try
        {
            _ = KiroUsageParser.Parse(output, DateTimeOffset.UtcNow);
            return true;
        }
        catch (ProviderException exception) when (exception.Category == ProviderErrorCategory.InvalidResponse)
        {
            return false;
        }
        catch (ProviderException)
        {
            return true;
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
            while (true)
            {
                int read = await session.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                lock (captureLock)
                {
                    AppendBounded(captured, Encoding.UTF8.GetString(buffer, 0, read));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static void AppendBounded(StringBuilder output, string value)
    {
        if (output.Length + value.Length > MaximumResponseLength)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Kiro returned a usage response that was too large to process safely.");
        }

        output.Append(value);
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
