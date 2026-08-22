using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Providers.Copilot;

public sealed class CopilotUsageProvider(
    IBoundedProcessRunner processRunner,
    IExecutableLocator executableLocator,
    TimeProvider? timeProvider = null,
    ICliVersionReader? cliVersionReader = null) : IUsageProvider, ICliVersionProvider
{
    private const int MaximumResponseLength = 1_048_576;
    private const int MaximumStandardErrorLength = 16_384;
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
            ProcessRunResult result = await processRunner.RunAsync(
                spec,
                MaximumResponseLength,
                MaximumStandardErrorLength,
                timeout.Token).ConfigureAwait(false);
            string response = Encoding.UTF8.GetString(result.StandardOutput);

            if (result.ExitCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(response))
                {
                    try
                    {
                        _ = CopilotUsageParser.Parse(response, this._timeProvider.GetUtcNow());
                    }
                    catch (ProviderException exception)
                        when (exception.Category == ProviderErrorCategory.AuthenticationRequired)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is ProviderException or ArgumentException)
                    {
                    }
                }

                throw new ProviderException(
                    ProviderErrorCategory.Unavailable,
                    "GitHub Copilot usage could not be read.");
            }

            if (string.IsNullOrWhiteSpace(response))
            {
                throw new ProviderException(
                    ProviderErrorCategory.AuthenticationRequired,
                    "GitHub needs you to sign in. Run `gh auth login`, then refresh.");
            }

            return CopilotUsageParser.Parse(response, this._timeProvider.GetUtcNow());
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
        catch (ProcessOutputLimitExceededException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "GitHub returned a Copilot usage response that was too large to process safely.",
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
