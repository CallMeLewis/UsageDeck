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
