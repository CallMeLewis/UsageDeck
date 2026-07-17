using System.Text.RegularExpressions;

namespace CodexBarWin.Infrastructure.Processes;

public interface ICliVersionReader
{
    Task<string?> ReadAsync(ProcessStartSpec spec, CancellationToken cancellationToken);
}

public sealed partial class CliVersionReader(IProcessSessionFactory sessionFactory) : ICliVersionReader
{
    private const int MaximumLineLength = 512;
    private const int MaximumLines = 8;

    public async Task<string?> ReadAsync(ProcessStartSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            await using IProcessSession session = sessionFactory.Start(spec);
            for (int lineNumber = 0; lineNumber < MaximumLines; lineNumber++)
            {
                string? line = await session.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null)
                {
                    return null;
                }

                if (line.Length > MaximumLineLength)
                {
                    return null;
                }

                Match match = SemanticVersionRegex().Match(line);
                if (match.Success)
                {
                    return match.Groups["version"].Value;
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or IOException
            or UnauthorizedAccessException
            or OperationCanceledException)
        {
            return null;
        }
    }

    [GeneratedRegex(
        @"(?<![0-9A-Za-z])v?(?<version>[0-9]+(?:\.[0-9]+){1,3}(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)(?![0-9A-Za-z])",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();
}
