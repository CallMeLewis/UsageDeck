using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Providers.Copilot;

public sealed class CopilotUsageProvider(
    IProcessSessionFactory processSessionFactory,
    IExecutableLocator executableLocator,
    TimeProvider? timeProvider = null,
    ICliVersionReader? cliVersionReader = null) : IUsageProvider, ICliVersionProvider
{
    private const int MaximumResponseLength = 1_048_576;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ProviderId Id => ProviderId.Copilot;

    public string DisplayName => "GitHub Copilot";

    public async Task<string?> ReadCliVersionAsync(CancellationToken cancellationToken)
    {
        string? executablePath = executableLocator.FindExecutable("gh");
        if (executablePath is null || cliVersionReader is null)
        {
            return null;
        }

        return await cliVersionReader.ReadAsync(
            new ProcessStartSpec(executablePath, ["--version"]),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        string? executablePath = executableLocator.FindExecutable("gh");
        if (executablePath is null)
        {
            throw new ProviderException(
                ProviderErrorCategory.NotInstalled,
                "GitHub CLI is not installed or `gh` is not on PATH.");
        }

        ProcessStartSpec spec = new(
            executablePath,
            [
                "api",
                "/copilot_internal/user",
                "--method",
                "GET",
                "-H",
                "Accept: application/vnd.github+json",
                "-H",
                "X-GitHub-Api-Version: 2025-04-01",
            ],
            Environment: new Dictionary<string, string?>
            {
                ["GH_PROMPT_DISABLED"] = "1",
                ["NO_COLOR"] = "1",
            });

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        try
        {
            await using IProcessSession session = processSessionFactory.Start(spec);
            StringBuilder response = new();
            while (await session.ReadLineAsync(timeout.Token).ConfigureAwait(false) is string line)
            {
                if (response.Length + line.Length + 1 > MaximumResponseLength)
                {
                    throw new ProviderException(
                        ProviderErrorCategory.InvalidResponse,
                        "GitHub returned a Copilot usage response that was too large to process safely.");
                }

                response.AppendLine(line);
            }

            if (response.Length == 0)
            {
                throw new ProviderException(
                    ProviderErrorCategory.AuthenticationRequired,
                    "GitHub needs you to sign in. Run `gh auth login`, then refresh.");
            }

            return CopilotUsageParser.Parse(response.ToString(), this._timeProvider.GetUtcNow());
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Transient,
                "GitHub did not return Copilot usage in time.",
                exception);
        }
        catch (Exception exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                "GitHub Copilot usage could not be read.",
                exception);
        }
    }
}
