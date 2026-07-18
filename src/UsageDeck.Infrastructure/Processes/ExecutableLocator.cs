namespace UsageDeck.Infrastructure.Processes;

public interface IExecutableLocator
{
    string? FindExecutable(string executableName);
}

public sealed record ExecutableSearchRoots(
    string UserProfile,
    string LocalApplicationData,
    string ApplicationData,
    string ProgramFiles,
    string ProgramFilesX86,
    string CommonApplicationData)
{
    public static ExecutableSearchRoots FromEnvironment() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
}

public sealed class ExecutableLocator : IExecutableLocator
{
    private const int MaximumPackageExecutables = 32;
    private readonly string? _path;
    private readonly ExecutableSearchRoots _roots;

    public ExecutableLocator(
        string? path = null,
        ExecutableSearchRoots? roots = null)
    {
        this._path = path;
        this._roots = roots ?? ExecutableSearchRoots.FromEnvironment();
    }

    public string? FindExecutable(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        string fileName = Path.HasExtension(executableName)
            ? executableName
            : executableName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);

        foreach (string directory in this.EnumeratePathDirectories())
        {
            string? candidate = GetCandidate(directory, fileName);
            if (candidate is not null && File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (string candidate in this.EnumerateKnownWindowsCandidates(executableName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IEnumerable<string> EnumeratePathDirectories()
    {
        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        foreach (string pathValue in this.EnumeratePathValues())
        {
            foreach (string directory in pathValue.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string expanded = Environment.ExpandEnvironmentVariables(directory.Trim('"'));
                if (!string.IsNullOrWhiteSpace(expanded) && directories.Add(expanded))
                {
                    yield return expanded;
                }
            }
        }
    }

    private IEnumerable<string> EnumeratePathValues()
    {
        if (this._path is not null)
        {
            yield return this._path;
            yield break;
        }

        string? processPath = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            yield return processPath;
        }

        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        string? userPath = TryGetPath(EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(userPath))
        {
            yield return userPath;
        }

        string? machinePath = TryGetPath(EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrWhiteSpace(machinePath))
        {
            yield return machinePath;
        }
    }

    private IEnumerable<string> EnumerateKnownWindowsCandidates(string executableName)
    {
        string normalizedName = Path.GetFileNameWithoutExtension(executableName).ToLowerInvariant();
        switch (normalizedName)
        {
            case "codex":
                yield return PathFrom(this._roots.LocalApplicationData, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
                yield return PathFrom(this._roots.UserProfile, ".codex", "packages", "standalone", "current", "bin", "codex.exe");
                foreach (string candidate in this.FindNpmPackageExecutables("codex.exe", "@openai", "codex"))
                {
                    yield return candidate;
                }

                break;
            case "claude":
                yield return PathFrom(this._roots.UserProfile, ".local", "bin", "claude.exe");
                foreach (string candidate in this.FindNpmPackageExecutables("claude.exe", "@anthropic-ai", "claude-code"))
                {
                    yield return candidate;
                }

                break;
            case "agy":
                yield return PathFrom(this._roots.LocalApplicationData, "agy", "bin", "agy.exe");
                break;
            case "gh":
                yield return PathFrom(this._roots.ProgramFiles, "GitHub CLI", "gh.exe");
                yield return PathFrom(this._roots.ProgramFilesX86, "GitHub CLI", "gh.exe");
                yield return PathFrom(this._roots.LocalApplicationData, "Programs", "GitHub CLI", "gh.exe");
                yield return PathFrom(this._roots.UserProfile, "scoop", "shims", "gh.exe");
                yield return PathFrom(this._roots.CommonApplicationData, "chocolatey", "bin", "gh.exe");
                break;
            case "kiro-cli":
                yield return PathFrom(this._roots.ProgramFiles, "Kiro-Cli", "kiro-cli.exe");
                yield return PathFrom(this._roots.ProgramFilesX86, "Kiro-Cli", "kiro-cli.exe");
                break;
            case "amp":
                yield return PathFrom(this._roots.UserProfile, ".amp", "bin", "amp.exe");
                yield return PathFrom(this._roots.UserProfile, ".local", "bin", "amp.exe");
                foreach (string candidate in this.FindNpmPackageExecutables("amp.exe", "@ampcode", "cli"))
                {
                    yield return candidate;
                }

                break;
            case "opencode":
                yield return PathFrom(this._roots.UserProfile, "bin", "opencode.exe");
                yield return PathFrom(this._roots.UserProfile, ".opencode", "bin", "opencode.exe");
                yield return PathFrom(this._roots.UserProfile, "scoop", "shims", "opencode.exe");
                yield return PathFrom(this._roots.CommonApplicationData, "chocolatey", "bin", "opencode.exe");
                foreach (string candidate in this.FindNpmPackageExecutables("opencode.exe", "opencode-ai"))
                {
                    yield return candidate;
                }

                break;
        }
    }

    private string[] FindNpmPackageExecutables(
        string executableName,
        params string[] packagePath)
    {
        if (string.IsNullOrWhiteSpace(this._roots.ApplicationData))
        {
            return [];
        }

        string packageRoot = Path.Combine(
            [this._roots.ApplicationData, "npm", "node_modules", .. packagePath]);
        if (!Directory.Exists(packageRoot))
        {
            return [];
        }

        try
        {
            EnumerationOptions options = new()
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 8,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            return Directory
                .EnumerateFiles(packageRoot, executableName, options)
                .Take(MaximumPackageExecutables)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? GetCandidate(string directory, string fileName)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(directory, fileName));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string PathFrom(string root, params string[] parts) =>
        string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine([root, .. parts]);

    private static string? TryGetPath(EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariable("PATH", target);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
