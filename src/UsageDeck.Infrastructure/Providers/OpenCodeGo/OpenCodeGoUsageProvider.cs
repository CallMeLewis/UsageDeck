using System.Net;
using System.Net.Http.Headers;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Security;
using UsageDeck.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace UsageDeck.Infrastructure.Providers.OpenCodeGo;

public sealed class OpenCodeGoUsageProvider(
    OpenCodeGoDataLocator dataLocator,
    IOpenCodeGoUsageReader usageReader,
    IExecutableLocator? executableLocator = null,
    ICliVersionReader? cliVersionReader = null,
    TimeProvider? timeProvider = null,
    HttpClient? httpClient = null,
    IOpenCodeGoApiKeySource? apiKeySource = null,
    Func<OpenCodeGoUsageRange>? usageRange = null) : IUsageProvider, ICliVersionProvider
{
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private static readonly Uri UsageEndpoint = new("https://console.opencode.ai/api/v1/usage/export");
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ProviderId Id => ProviderId.OpenCodeGo;

    public string DisplayName => ProviderId.OpenCodeGo.DisplayName;

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        string? apiKey;
        try
        {
            apiKey = apiKeySource?.ReadApiKey()?.Trim();
        }
        catch (SecretStoreException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                exception.SafeMessage,
                exception);
        }

        if (!string.IsNullOrWhiteSpace(apiKey) && httpClient is not null && usageRange is not null)
        {
            return await this.FetchApiUsageAsync(apiKey, usageRange(), cancellationToken).ConfigureAwait(false);
        }

        return await this.FetchLocalUsageAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderSnapshot> FetchLocalUsageAsync(CancellationToken cancellationToken)
    {
        string? databasePath = dataLocator.FindDatabasePath();
        if (databasePath is null)
        {
            throw new ProviderException(
                ProviderErrorCategory.NotInstalled,
                "OpenCode Go usage was not found. Add an OpenCode Console service-account key in Settings or use OpenCode Go once, then refresh.");
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

    private async Task<ProviderSnapshot> FetchApiUsageAsync(
        string apiKey,
        OpenCodeGoUsageRange range,
        CancellationToken cancellationToken)
    {
        Uri endpoint = new($"{UsageEndpoint}?scope=organization&range={OpenCodeGoUsageExportParser.ApiRange(range)}");
        using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            using HttpResponseMessage response = await httpClient!.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ProviderException(
                    ProviderErrorCategory.AuthenticationRequired,
                    "OpenCode Console rejected the service-account key. Check it in Settings, then refresh.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new ProviderException(
                    ProviderErrorCategory.Transient,
                    "OpenCode Console API billing is temporarily unavailable.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderException(
                    ProviderErrorCategory.Unavailable,
                    "OpenCode Console could not return API billing usage right now.");
            }

            byte[] body = await ReadBoundedResponseAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return OpenCodeGoUsageExportParser.Parse(body, this._timeProvider.GetUtcNow(), range);
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
                "OpenCode Console did not return API billing usage in time.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Transient,
                "OpenCode Console API billing could not be reached.",
                exception);
        }
        catch (IOException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Transient,
                "OpenCode Console API billing could not be read.",
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

    private static async Task<byte[]> ReadBoundedResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        long? length = content.Headers.ContentLength;
        if (length > MaximumResponseBytes)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "OpenCode Console returned a usage export that was too large to process safely.");
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream result = new(length is > 0 ? (int)length.Value : 16_384);
        byte[] buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaximumResponseBytes)
            {
                throw new ProviderException(
                    ProviderErrorCategory.InvalidResponse,
                    "OpenCode Console returned a usage export that was too large to process safely.");
            }

            result.Write(buffer, 0, read);
        }

        return result.ToArray();
    }
}
