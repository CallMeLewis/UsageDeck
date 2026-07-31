namespace UsageDeck.Infrastructure.Processes;

public sealed record ProcessStartSpec(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null);

public interface IProcessSession : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    Task<string?> ReadLineAsync(CancellationToken cancellationToken);
}
public interface IProcessSessionFactory
{
    IProcessSession Start(ProcessStartSpec spec);
}

public sealed record ProcessRunResult(
    byte[] StandardOutput,
    int ExitCode,
    string StandardError);

public interface IBoundedProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        ProcessStartSpec spec,
        int maximumStandardOutputBytes,
        int maximumStandardErrorBytes,
        CancellationToken cancellationToken);
}

public sealed class ProcessOutputLimitExceededException : IOException
{
    public ProcessOutputLimitExceededException(int maximumBytes)
        : base("Provider process output exceeded the configured safety limit.")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        this.MaximumBytes = maximumBytes;
    }

    public int MaximumBytes { get; }
}
