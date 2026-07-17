namespace CodexBarWin.Infrastructure.Processes;

public interface IExecutableLocator
{
    string? FindExecutable(string executableName);
}
public sealed class ExecutableLocator : IExecutableLocator
{
    private readonly string _path;

    public ExecutableLocator(string? path = null)
    {
        this._path = path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    }

    public string? FindExecutable(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        string fileName = Path.HasExtension(executableName)
            ? executableName
            : executableName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);

        foreach (string directory in this._path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(directory, fileName));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
