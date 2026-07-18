using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Providers.Codex;

public sealed class CodexProcessSpecFactory(IExecutableLocator executableLocator)
{
    private static readonly string[] CodexArguments = ["-s", "read-only", "-a", "untrusted", "app-server"];

    public ProcessStartSpec Create(ProviderHost host) => this.Create(host, CodexArguments);

    public ProcessStartSpec CreateVersion(ProviderHost host) => this.Create(host, ["--version"]);

    private ProcessStartSpec Create(ProviderHost host, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (host.Kind == ProviderHostKind.Wsl)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new ProviderException(ProviderErrorCategory.Unavailable, "WSL is available only on Windows.");
            }

            string wslPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wsl.exe");
            if (!File.Exists(wslPath))
            {
                throw new ProviderException(ProviderErrorCategory.NotInstalled, "WSL is not installed.");
            }

            return new ProcessStartSpec(
                wslPath,
                ["--distribution", host.WslDistribution!, "--exec", "codex", .. arguments]);
        }

        string? codexPath = executableLocator.FindExecutable("codex");
        if (codexPath is null)
        {
            throw new ProviderException(
                ProviderErrorCategory.NotInstalled,
                "Codex CLI was not found. Install Codex and sign in, then refresh.");
        }

        return new ProcessStartSpec(codexPath, arguments);
    }
}
