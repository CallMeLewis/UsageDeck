using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Tests;

public sealed class ExecutableLocatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "UsageDeck.Tests", Guid.NewGuid().ToString("N"));

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

    [Theory]
    [InlineData("codex", "user", ".codex/packages/standalone/current/bin/codex.exe")]
    [InlineData("claude", "user", ".local/bin/claude.exe")]
    [InlineData("agy", "local", "agy/bin/agy.exe")]
    [InlineData("gh", "programs", "GitHub CLI/gh.exe")]
    [InlineData("kiro-cli", "programs", "Kiro-Cli/kiro-cli.exe")]
    [InlineData("amp", "user", ".amp/bin/amp.exe")]
    [InlineData("opencode", "user", ".opencode/bin/opencode.exe")]
    public void FindExecutableChecksKnownWindowsInstallLocations(
        string executableName,
        string rootName,
        string relativePath)
    {
        string userProfile = Path.Combine(this._directory, "user");
        string localApplicationData = Path.Combine(this._directory, "local");
        string applicationData = Path.Combine(this._directory, "roaming");
        string programFiles = Path.Combine(this._directory, "programs");
        string programFilesX86 = Path.Combine(this._directory, "programs-x86");
        string commonApplicationData = Path.Combine(this._directory, "common");
        Dictionary<string, string> roots = new(StringComparer.Ordinal)
        {
            ["user"] = userProfile,
            ["local"] = localApplicationData,
            ["roaming"] = applicationData,
            ["programs"] = programFiles,
            ["programs-x86"] = programFilesX86,
            ["common"] = commonApplicationData,
        };
        string executablePath = Path.Combine(
            roots[rootName],
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllText(executablePath, string.Empty);
        ExecutableSearchRoots searchRoots = new(
            userProfile,
            localApplicationData,
            applicationData,
            programFiles,
            programFilesX86,
            commonApplicationData);

        string? result = new ExecutableLocator(string.Empty, searchRoots).FindExecutable(executableName);

        Assert.Equal(executablePath, result);
    }

    [Theory]
    [InlineData("codex", "@openai/codex/vendor/x86_64-pc-windows-msvc/codex/codex.exe")]
    [InlineData("claude", "@anthropic-ai/claude-code/vendor/claude.exe")]
    [InlineData("amp", "@ampcode/cli/vendor/amp.exe")]
    [InlineData("opencode", "opencode-ai/node_modules/opencode-windows-x64/bin/opencode.exe")]
    public void FindExecutableChecksBundledNpmExecutables(string executableName, string packageRelativePath)
    {
        string applicationData = Path.Combine(this._directory, "roaming");
        string executablePath = Path.Combine(
            applicationData,
            "npm",
            "node_modules",
            packageRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllText(executablePath, string.Empty);
        ExecutableSearchRoots searchRoots = new(
            Path.Combine(this._directory, "user"),
            Path.Combine(this._directory, "local"),
            applicationData,
            Path.Combine(this._directory, "programs"),
            Path.Combine(this._directory, "programs-x86"),
            Path.Combine(this._directory, "common"));

        string? result = new ExecutableLocator(string.Empty, searchRoots).FindExecutable(executableName);

        Assert.Equal(executablePath, result);
    }

    public void Dispose()
    {
        if (Directory.Exists(this._directory))
        {
            Directory.Delete(this._directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
