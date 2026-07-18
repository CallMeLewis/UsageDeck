using Porta.Pty;

namespace UsageDeck.Infrastructure.Processes;

public sealed record PtyStartSpec(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null,
    int Columns = 120,
    int Rows = 40);

public interface IPtySession : IAsyncDisposable
{
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

    void Kill();
}
public interface IPtySessionFactory
{
    Task<IPtySession> StartAsync(PtyStartSpec spec, CancellationToken cancellationToken);
}

public sealed class PtySessionFactory : IPtySessionFactory
{
    public async Task<IPtySession> StartAsync(PtyStartSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);

        PtyOptions options = new()
        {
            Name = "UsageDeck provider probe",
            App = spec.ExecutablePath,
            CommandLine = spec.Arguments.ToArray(),
            Cwd = spec.WorkingDirectory,
            Cols = spec.Columns,
            Rows = spec.Rows,
            Environment = spec.Environment?.ToDictionary() ?? new Dictionary<string, string>(),
        };

        IPtyConnection connection = await PtyProvider.SpawnAsync(options, cancellationToken).ConfigureAwait(false);
        return new PtySession(connection);
    }

    private sealed class PtySession(IPtyConnection connection) : IPtySession
    {
        private bool _disposed;

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
            return await connection.ReaderStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
            await connection.WriterStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await connection.WriterStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Kill()
        {
            if (!this._disposed)
            {
                connection.Kill();
            }
        }

        public ValueTask DisposeAsync()
        {
            if (this._disposed)
            {
                return ValueTask.CompletedTask;
            }

            this._disposed = true;
            connection.Kill();
            connection.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
