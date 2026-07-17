using CodexBarWin.Infrastructure.Processes;
using CodexBarWin.Infrastructure.Providers.Codex;

namespace CodexBarWin.Infrastructure.Tests;

public sealed class ExecutableLocatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CodexBarWin.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FindExecutableFindsExistingPathEntry()
    {
        Directory.CreateDirectory(this._directory);
        string executableName = OperatingSystem.IsWindows() ? "provider.exe" : "provider";
        string executablePath = Path.Combine(this._directory, executableName);
        File.WriteAllText(executablePath, string.Empty);
        string path = string.Join(Path.PathSeparator, "::invalid::", this._directory);

        string? result = new ExecutableLocator(path).FindExecutable("provider");

        Assert.Equal(executablePath, result);
    }

    [Fact]
    public void CodexLocatorFallsBackToProvidedPathLocator()
    {
        StubExecutableLocator fallback = new("C:\\safe\\codex.exe");
        CodexExecutableLocator locator = new(fallback);

        string? result = locator.FindExecutable("different-tool");

        Assert.Equal("C:\\safe\\codex.exe", result);
        Assert.Equal("different-tool", fallback.LastRequestedName);
    }

    public void Dispose()
    {
        if (Directory.Exists(this._directory))
        {
            Directory.Delete(this._directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class StubExecutableLocator(string path) : IExecutableLocator
    {
        public string? LastRequestedName { get; private set; }

        public string? FindExecutable(string executableName)
        {
            this.LastRequestedName = executableName;
            return path;
        }
    }
}
