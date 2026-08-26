using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Providers.Codex;

public sealed class CodexProcessSpecFactory(IExecutableLocator executableLocator)
{
    private static readonly string[] CodexArguments = ["-s", "read-only", "-a", "never", "app-server"];

    public ProcessStartSpec Create() => this.Create(CodexArguments);

    public ProcessStartSpec CreateVersion() => this.Create(["--version"]);

    private ProcessStartSpec Create(IReadOnlyList<string> arguments)
    {
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
