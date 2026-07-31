using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Providers.TheClawBay;

public sealed record TheClawBayCliCommand(
    string ExecutablePath,
    IReadOnlyList<string> PrefixArguments);

public interface ITheClawBayCliCommandResolver
{
    TheClawBayCliCommand? Resolve();
}

public sealed class TheClawBayCliCommandResolver(IExecutableLocator executableLocator)
    : ITheClawBayCliCommandResolver
{
    public TheClawBayCliCommand? Resolve()
    {
        string? directExecutable = executableLocator.FindExecutable("theclawbay");
        if (IsDirectlyExecutable(directExecutable))
        {
            return new TheClawBayCliCommand(directExecutable!, []);
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string? shimPath = executableLocator.FindExecutable("theclawbay.cmd");
        string? shimDirectory = Path.GetDirectoryName(shimPath);
        if (string.IsNullOrWhiteSpace(shimDirectory))
        {
            return null;
        }

        string packageEntryPath = Path.GetFullPath(Path.Combine(
            shimDirectory,
            "node_modules",
            "theclawbay",
            "dist",
            "index.js"));
        if (!File.Exists(packageEntryPath))
        {
            return null;
        }

        string localNodePath = Path.GetFullPath(Path.Combine(shimDirectory, "node.exe"));
        string? nodePath = File.Exists(localNodePath)
            ? localNodePath
            : executableLocator.FindExecutable("node");
        return nodePath is null
            ? null
            : new TheClawBayCliCommand(nodePath, [packageEntryPath]);
    }

    private static bool IsDirectlyExecutable(string? path) => path is not null
        && (!OperatingSystem.IsWindows()
            || string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase));
}
