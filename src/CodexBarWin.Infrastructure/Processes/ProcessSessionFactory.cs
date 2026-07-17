using System.Diagnostics;
using System.Text;

namespace CodexBarWin.Infrastructure.Processes;

public sealed class ProcessSessionFactory : IProcessSessionFactory
{
    public IProcessSession Start(ProcessStartSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ExecutablePath);

        ProcessStartInfo startInfo = new()
        {
            FileName = spec.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (string argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (spec.Environment is not null)
        {
            foreach ((string key, string? value) in spec.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The provider process did not start.");
            }

            return new ProcessSession(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private sealed class ProcessSession : IProcessSession
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _stderrCancellation = new();
        private readonly Task _stderrDrain;
        private bool _disposed;

        public ProcessSession(Process process)
        {
            this._process = process;
            this._stderrDrain = DrainStandardErrorAsync(process.StandardError, this._stderrCancellation.Token);
        }

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
            ArgumentNullException.ThrowIfNull(line);

            await this._process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await this._process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
            return await this._process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this._process.StandardInput.Close();

            if (!this._process.HasExited)
            {
                this._process.Kill(entireProcessTree: true);
            }

            await this._process.WaitForExitAsync().ConfigureAwait(false);
            await this._stderrCancellation.CancelAsync().ConfigureAwait(false);
            await this._stderrDrain.ConfigureAwait(false);

            this._stderrCancellation.Dispose();
            this._process.Dispose();
        }

        private static async Task DrainStandardErrorAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            char[] buffer = new char[2048];
            try
            {
                while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
                {
                    // Provider stderr can contain sensitive context. Drain it to prevent deadlock but never retain it.
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }
}
