using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Providers.Codex;

public sealed class CodexExecutableLocator(IExecutableLocator pathLocator) : IExecutableLocator
{
    private const int MaxKnownPathCandidates = 32;

    public string? FindExecutable(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        if (OperatingSystem.IsWindows() && string.Equals(executableName, "codex", StringComparison.OrdinalIgnoreCase))
        {
            string? npmExecutable = FindNpmExecutable();
            if (npmExecutable is not null)
            {
                return npmExecutable;
            }
        }

        return pathLocator.FindExecutable(executableName);
    }

    private static string? FindNpmExecutable()
    {
        string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(applicationData))
        {
            return null;
        }

        string packageRoot = Path.Combine(applicationData, "npm", "node_modules", "@openai", "codex");
        if (!Directory.Exists(packageRoot))
        {
            return null;
        }

        try
        {
            return Directory
                .EnumerateFiles(packageRoot, "codex.exe", SearchOption.AllDirectories)
                .Take(MaxKnownPathCandidates)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
