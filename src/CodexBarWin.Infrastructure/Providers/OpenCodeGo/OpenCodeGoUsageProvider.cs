using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Processes;
using Microsoft.Data.Sqlite;

namespace CodexBarWin.Infrastructure.Providers.OpenCodeGo;

public sealed class OpenCodeGoUsageProvider(
    OpenCodeGoDataLocator dataLocator,
    IOpenCodeGoUsageReader usageReader,
    IExecutableLocator? executableLocator = null,
    ICliVersionReader? cliVersionReader = null,
    TimeProvider? timeProvider = null) : IUsageProvider, ICliVersionProvider
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ProviderId Id => ProviderId.OpenCodeGo;

    public string DisplayName => ProviderId.OpenCodeGo.DisplayName;

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        string? databasePath = dataLocator.FindDatabasePath();
        if (databasePath is null)
        {
            throw new ProviderException(
                ProviderErrorCategory.NotInstalled,
                "OpenCode Go local history was not found. Use OpenCode Go once, then refresh.");
        }

        try
        {
            return await Task.Run(
                () => usageReader.Read(databasePath, this._timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                "OpenCode Go local usage could not be read while its database is busy or unavailable.",
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                "OpenCode Go local usage could not be read.",
                exception);
        }
    }

    public async Task<string?> ReadCliVersionAsync(CancellationToken cancellationToken)
    {
        string? executablePath = executableLocator?.FindExecutable("opencode");
        if (executablePath is null || cliVersionReader is null)
        {
            return null;
        }

        return await cliVersionReader.ReadAsync(
            new ProcessStartSpec(executablePath, ["--version"]),
            cancellationToken).ConfigureAwait(false);
    }
}
