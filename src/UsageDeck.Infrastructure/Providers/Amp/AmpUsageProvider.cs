using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Providers.Amp;

public sealed class AmpUsageProvider(
    IBoundedProcessRunner processRunner,
    IExecutableLocator executableLocator,
    TimeProvider? timeProvider = null,
    string? userProfile = null,
    ICliVersionReader? cliVersionReader = null) : IUsageProvider, ICliVersionProvider
{
    private const int MaximumResponseLength = 262_144;
    private const int MaximumStandardErrorLength = 16_384;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly string _userProfile = userProfile
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public ProviderId Id => ProviderId.Amp;

    public string DisplayName => ProviderId.Amp.DisplayName;

    public async Task<string?> ReadCliVersionAsync(CancellationToken cancellationToken)
    {
        string? executablePath = this.FindExecutable();
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
        string? executablePath = this.FindExecutable();
        if (executablePath is null)
        {
            throw new ProviderException(
                ProviderErrorCategory.NotInstalled,
                "Amp CLI is not installed or `amp` is not on PATH.");
        }

        ProcessStartSpec spec = new(
            executablePath,
            ["usage"],
            Environment: new Dictionary<string, string?>
            {
                ["NO_COLOR"] = "1",
                ["TERM"] = "dumb",
            });

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

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
                        _ = AmpUsageParser.Parse(response, this._timeProvider.GetUtcNow());
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
                    "Amp usage could not be read.");
            }

            if (string.IsNullOrWhiteSpace(response))
            {
                throw new ProviderException(
                    ProviderErrorCategory.AuthenticationRequired,
                    "Amp needs you to sign in. Run `amp login`, then refresh.");
            }

            return AmpUsageParser.Parse(response, this._timeProvider.GetUtcNow());
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
                "Amp did not return usage details in time.",
                exception);
        }
        catch (ProcessOutputLimitExceededException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Amp returned a usage response that was too large to process safely.",
                exception);
        }
        catch (Exception exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                "Amp usage could not be read.",
                exception);
        }
    }

    private string? FindExecutable()
    {
        string? overridePath = Environment.GetEnvironmentVariable("AMP_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        string? located = executableLocator.FindExecutable("amp");
        if (located is not null)
        {
            return located;
        }

        string[] wellKnownPaths =
        [
            Path.Combine(this._userProfile, ".amp", "bin", "amp.exe"),
            Path.Combine(this._userProfile, ".local", "bin", "amp.exe"),
        ];
        return wellKnownPaths.FirstOrDefault(File.Exists);
    }
}
