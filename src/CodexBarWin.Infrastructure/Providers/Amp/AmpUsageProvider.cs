using System.Text;
using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Processes;

namespace CodexBarWin.Infrastructure.Providers.Amp;

public sealed class AmpUsageProvider(
    IProcessSessionFactory processSessionFactory,
    IExecutableLocator executableLocator,
    TimeProvider? timeProvider = null,
    string? userProfile = null,
    ICliVersionReader? cliVersionReader = null) : IUsageProvider, ICliVersionProvider
{
    private const int MaximumResponseLength = 262_144;
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
            await using IProcessSession session = processSessionFactory.Start(spec);
            StringBuilder response = new(capacity: 4096);
            while (await session.ReadLineAsync(timeout.Token).ConfigureAwait(false) is string line)
            {
                if (response.Length + line.Length + Environment.NewLine.Length > MaximumResponseLength)
                {
                    throw new ProviderException(
                        ProviderErrorCategory.InvalidResponse,
                        "Amp returned a usage response that was too large to process safely.");
                }

                response.AppendLine(line);
            }

            if (response.Length == 0)
            {
                throw new ProviderException(
                    ProviderErrorCategory.AuthenticationRequired,
                    "Amp needs you to sign in. Run `amp login`, then refresh.");
            }

            return AmpUsageParser.Parse(response.ToString(), this._timeProvider.GetUtcNow());
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
