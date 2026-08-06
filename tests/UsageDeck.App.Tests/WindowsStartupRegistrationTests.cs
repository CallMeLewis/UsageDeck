namespace UsageDeck.App.Tests;

public sealed class WindowsStartupRegistrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "UsageDeck.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResolveExecutablePathPrefersStableInstalledLauncher()
    {
        string installRoot = Path.Combine(this._directory, "install");
        string appDirectory = Path.Combine(installRoot, "current");
        string installedLauncher = CreateFile(installRoot, "UsageDeck.Bootstrap.exe");
        _ = CreateFile(appDirectory, "UsageDeck.Bootstrap.exe");

        string result = WindowsStartupRegistration.ResolveExecutablePath(
            appDirectory,
            installRoot,
            Path.Combine(appDirectory, "UsageDeck.App.exe"));

        Assert.Equal(installedLauncher, result);
    }

    [Fact]
    public void ResolveExecutablePathUsesBundledLauncherForPortableCopy()
    {
        string appDirectory = Path.Combine(this._directory, "portable");
        string bundledLauncher = CreateFile(appDirectory, "UsageDeck.Bootstrap.exe");

        string result = WindowsStartupRegistration.ResolveExecutablePath(
            appDirectory,
            installRootDirectory: null,
            processPath: Path.Combine(appDirectory, "UsageDeck.App.exe"));

        Assert.Equal(bundledLauncher, result);
    }

    [Fact]
    public void ResolveExecutablePathFallsBackToCurrentProcess()
    {
        string appDirectory = Path.Combine(this._directory, "development");
        string processPath = Path.Combine(appDirectory, "UsageDeck.App.exe");

        string result = WindowsStartupRegistration.ResolveExecutablePath(
            appDirectory,
            installRootDirectory: null,
            processPath: processPath);

        Assert.Equal(Path.GetFullPath(processPath), result);
    }

    public void Dispose()
    {
        if (Directory.Exists(this._directory))
        {
            Directory.Delete(this._directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static string CreateFile(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, string.Empty);
        return Path.GetFullPath(path);
    }
}
