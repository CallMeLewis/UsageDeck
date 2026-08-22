using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace UsageDeck.Infrastructure.Processes;

public sealed class ProcessSessionFactory : IProcessSessionFactory, IBoundedProcessRunner
{
    public IProcessSession Start(ProcessStartSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ExecutablePath);
        Process process = new() { StartInfo = CreateStartInfo(spec) };
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

    public async Task<ProcessRunResult> RunAsync(
        ProcessStartSpec spec,
        int maximumStandardOutputBytes,
        int maximumStandardErrorBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ExecutablePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumStandardOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumStandardErrorBytes);
        cancellationToken.ThrowIfCancellationRequested();

        using Process process = new() { StartInfo = CreateStartInfo(spec) };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start())
        {
            throw new InvalidOperationException("The provider process did not start.");
        }

        using CancellationTokenSource processCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        process.StandardInput.Close();
        Task<byte[]> standardOutput = ReadBoundedStandardOutputAsync(
            process.StandardOutput.BaseStream,
            maximumStandardOutputBytes,
            processCancellation.Token);
        Task<string> standardError = CaptureBoundedStandardErrorAsync(
            process.StandardError.BaseStream,
            maximumStandardErrorBytes,
            processCancellation.Token);
        Task exit = process.WaitForExitAsync(processCancellation.Token);

        try
        {
            List<Task> pending = [standardOutput, standardError, exit];
            while (pending.Count > 0)
            {
                Task completion = await Task.WhenAny(pending).ConfigureAwait(false);
                await completion.ConfigureAwait(false);
                pending.Remove(completion);
            }

            return new ProcessRunResult(
                await standardOutput.ConfigureAwait(false),
                process.ExitCode,
                await standardError.ConfigureAwait(false));
        }
        catch
        {
            await processCancellation.CancelAsync().ConfigureAwait(false);
            TryKill(process);
            await WaitForExitAfterFailureAsync(process).ConfigureAwait(false);
            await ObserveFailureAsync(standardOutput).ConfigureAwait(false);
            await ObserveFailureAsync(standardError).ConfigureAwait(false);
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(ProcessStartSpec spec)
    {
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

        return startInfo;
    }

    private static async Task<byte[]> ReadBoundedStandardOutputAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream output = new(capacity: Math.Min(maximumBytes, 4096));
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                throw new ProcessOutputLimitExceededException(maximumBytes);
            }

            output.Write(buffer, 0, read);
        }
    }

    private static async Task<string> CaptureBoundedStandardErrorAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream captured = new(capacity: Math.Min(maximumBytes, 4096));
        byte[] buffer = new byte[2048];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return Encoding.UTF8.GetString(captured.GetBuffer(), 0, checked((int)captured.Length));
            }

            int remaining = maximumBytes - checked((int)captured.Length);
            if (remaining > 0)
            {
                captured.Write(buffer, 0, Math.Min(read, remaining));
            }
        }
    }

    private static async Task ObserveFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original runner failure is rethrown after all secondary stream tasks are observed.
        }
    }

    private static async Task WaitForExitAfterFailureAsync(Process process)
    {
        try
        {
            using CancellationTokenSource cleanupTimeout = new(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
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
            try
            {
                try
                {
                    this._process.StandardInput.Close();
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or IOException or ObjectDisposedException)
                {
                }

                TryKill(this._process);
                await WaitForExitAfterFailureAsync(this._process).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await this._stderrCancellation.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }

                await ObserveFailureWithinCleanupTimeoutAsync(this._stderrDrain).ConfigureAwait(false);
                this._stderrCancellation.Dispose();
                this._process.Dispose();
            }
        }

        private static async Task ObserveFailureWithinCleanupTimeoutAsync(Task task)
        {
            Task observation = ObserveFailureAsync(task);
            try
            {
                await observation.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
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
