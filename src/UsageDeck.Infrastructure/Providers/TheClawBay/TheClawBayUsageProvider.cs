using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Security;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.Infrastructure.Providers.TheClawBay;

public sealed class TheClawBayUsageProvider(
    IProcessSessionFactory processSessionFactory,
    IExecutableLocator executableLocator,
    HttpClient httpClient,
    ITheClawBayApiKeySource apiKeySource,
    Func<TheClawBayUsageSource> usageSource,
    ICliVersionReader? cliVersionReader = null) : IUsageProvider, ICliVersionProvider
{
    private const int MaximumResponseBytes = 1_048_576;
    private static readonly Uri UsageEndpoint = new("https://theclawbay.com/api/codex-auth/v1/quota");

    public ProviderId Id => ProviderId.TheClawBay;

    public string DisplayName => ProviderId.TheClawBay.DisplayName;

    public Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken) => usageSource() switch
    {
        TheClawBayUsageSource.Automatic => this.FetchAutomaticAsync(cancellationToken),
        TheClawBayUsageSource.Cli => this.FetchFromCliAsync(cancellationToken),
        TheClawBayUsageSource.ApiKey => this.FetchFromApiAsync(cancellationToken),
        _ => throw new ProviderException(
            ProviderErrorCategory.Unavailable,
            "The configured TheClawBay usage source is unsupported."),
    };

    public async Task<string?> ReadCliVersionAsync(CancellationToken cancellationToken)
    {
        string? executablePath = executableLocator.FindExecutable("theclawbay");
        if (executablePath is null || cliVersionReader is null)
        {
            return null;
        }

        return await cliVersionReader.ReadAsync(
            new ProcessStartSpec(executablePath, ["--version"]),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderSnapshot> FetchAutomaticAsync(CancellationToken cancellationToken)
    {
        string? apiKey = null;
        ProviderException? apiFailure = null;
        try
        {
            apiKey = this.ReadApiKey();
            if (apiKey is not null)
            {
                return await this.FetchFromApiAsync(apiKey, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ProviderException exception) when (IsEligibleForFallback(exception))
        {
            apiFailure = exception;
        }

        try
        {
            return await this.FetchFromCliAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException exception) when (exception.Category != ProviderErrorCategory.InvalidResponse)
        {
            if (apiKey is null
                && apiFailure is null
                && exception.Category == ProviderErrorCategory.NotInstalled)
            {
                throw new ProviderException(
                    ProviderErrorCategory.NotInstalled,
                    "No TheClawBay API key is configured and TheClawBay CLI was not found. Configure either source in Settings.");
            }

            throw CombinedFailure(apiFailure, exception);
        }
    }

    private async Task<ProviderSnapshot> FetchFromApiAsync(CancellationToken cancellationToken)
    {
        string? apiKey = this.ReadApiKey();
        if (apiKey is null)
        {
            throw new ProviderException(
                ProviderErrorCategory.AuthenticationRequired,
                "Add a TheClawBay API key in Settings, then refresh.");
        }

        return await this.FetchFromApiAsync(apiKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderSnapshot> FetchFromApiAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ProviderException(
                    ProviderErrorCategory.AuthenticationRequired,
                    "TheClawBay rejected the API key. Check it in Settings, then refresh.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new ProviderException(
                    ProviderErrorCategory.Transient,
                    "TheClawBay usage is temporarily unavailable.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderException(
                    ProviderErrorCategory.Unavailable,
                    "TheClawBay could not return usage right now.");
            }

            byte[] body = await ReadBoundedResponseAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return TheClawBayUsageParser.Parse(body, "TheClawBay API");
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
                "TheClawBay did not return usage in time.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Transient,
                "TheClawBay usage could not be reached.",
                exception);
        }
        catch (IOException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Transient,
                "TheClawBay usage could not be read.",
                exception);
        }
    }

    private async Task<ProviderSnapshot> FetchFromCliAsync(CancellationToken cancellationToken)
    {
        string? executablePath = executableLocator.FindExecutable("theclawbay");
        if (executablePath is null)
        {
            throw new ProviderException(
                ProviderErrorCategory.NotInstalled,
                "TheClawBay CLI is not installed or is not on PATH.");
        }

        ProcessStartSpec spec = new(
            executablePath,
            ["usage", "--json"],
            Environment: new Dictionary<string, string?>
            {
                ["NO_COLOR"] = "1",
                ["TERM"] = "dumb",
            });
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            await using IProcessSession session = processSessionFactory.Start(spec);
            StringBuilder output = new(capacity: 4096);
            long outputBytes = 0;
            while (await session.ReadLineAsync(timeout.Token).ConfigureAwait(false) is string line)
            {
                outputBytes += Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
                if (outputBytes > MaximumResponseBytes)
                {
                    throw new ProviderException(
                        ProviderErrorCategory.InvalidResponse,
                        "TheClawBay CLI returned a usage response that was too large to process safely.");
                }

                output.AppendLine(line);
            }

            string response = output.ToString();
            if (string.IsNullOrWhiteSpace(response))
            {
                throw new ProviderException(
                    ProviderErrorCategory.AuthenticationRequired,
                    "Run theclawbay setup, then refresh.");
            }

            return TheClawBayUsageParser.Parse(
                Encoding.UTF8.GetBytes(response),
                "TheClawBay CLI");
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
                "TheClawBay CLI did not return usage in time.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or Win32Exception
            or IOException
            or UnauthorizedAccessException)
        {
            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                "TheClawBay CLI usage could not be read.",
                exception);
        }
    }

    private string? ReadApiKey()
    {
        try
        {
            string? apiKey = apiKeySource.ReadApiKey()?.Trim();
            return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        }
        catch (SecretStoreException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                exception.SafeMessage,
                exception);
        }
    }

    private static bool IsEligibleForFallback(ProviderException exception) => exception.Category is
        ProviderErrorCategory.AuthenticationRequired
        or ProviderErrorCategory.Transient
        or ProviderErrorCategory.Unavailable;

    private static ProviderErrorCategory CombinedCategory(
        ProviderException? apiFailure,
        ProviderException cliFailure)
    {
        ProviderErrorCategory[] categories = apiFailure is null
            ? [cliFailure.Category]
            : [apiFailure.Category, cliFailure.Category];
        ProviderErrorCategory[] precedence =
        [
            ProviderErrorCategory.AuthenticationRequired,
            ProviderErrorCategory.Transient,
            ProviderErrorCategory.InvalidResponse,
            ProviderErrorCategory.NotInstalled,
            ProviderErrorCategory.Unavailable,
        ];
        return precedence.First(categories.Contains);
    }

    private static ProviderException CombinedFailure(
        ProviderException? apiFailure,
        ProviderException cliFailure) => new(
            CombinedCategory(apiFailure, cliFailure),
            "No usable TheClawBay source was available. Check the API key or run theclawbay setup, then refresh.");

    private static async Task<byte[]> ReadBoundedResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        long? length = content.Headers.ContentLength;
        if (length > MaximumResponseBytes)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "TheClawBay returned a usage response that was too large to process safely.");
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream result = new(length is > 0 ? (int)length.Value : 4096);
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
                    "TheClawBay returned a usage response that was too large to process safely.");
            }

            result.Write(buffer, 0, read);
        }

        return result.ToArray();
    }
}
