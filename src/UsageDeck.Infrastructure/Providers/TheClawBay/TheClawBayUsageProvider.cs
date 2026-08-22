using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Security;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.Infrastructure.Providers.TheClawBay;

public sealed class TheClawBayUsageProvider : IUsageProvider, ICliVersionProvider
{
    private const int MaximumResponseBytes = 1_048_576;
    private const int MaximumStandardErrorBytes = 16_384;
    private const string OfficialMissingCredentialError =
        "Error: No saved credential found. Run \"theclawbay setup\" or pass --api-key.";
    private static readonly TimeSpan ApiRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CliRequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly Uri UsageEndpoint = new("https://theclawbay.com/api/codex-auth/v1/quota");
    private readonly ITheClawBayApiKeySource _apiKeySource;
    private readonly TimeSpan _apiRequestTimeout;
    private readonly ITheClawBayCliCommandResolver _cliCommandResolver;
    private readonly TimeSpan _cliRequestTimeout;
    private readonly ICliVersionReader? _cliVersionReader;
    private readonly HttpClient _httpClient;
    private readonly IBoundedProcessRunner _processRunner;
    private readonly Func<TheClawBayUsageSource> _usageSource;

    public TheClawBayUsageProvider(
        IBoundedProcessRunner processRunner,
        ITheClawBayCliCommandResolver cliCommandResolver,
        HttpClient httpClient,
        ITheClawBayApiKeySource apiKeySource,
        Func<TheClawBayUsageSource> usageSource,
        ICliVersionReader? cliVersionReader = null)
        : this(
            processRunner,
            cliCommandResolver,
            httpClient,
            apiKeySource,
            usageSource,
            cliVersionReader,
            ApiRequestTimeout,
            CliRequestTimeout)
    {
    }

    internal TheClawBayUsageProvider(
        IBoundedProcessRunner processRunner,
        ITheClawBayCliCommandResolver cliCommandResolver,
        HttpClient httpClient,
        ITheClawBayApiKeySource apiKeySource,
        Func<TheClawBayUsageSource> usageSource,
        ICliVersionReader? cliVersionReader,
        TimeSpan apiRequestTimeout,
        TimeSpan cliRequestTimeout)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(cliCommandResolver);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(apiKeySource);
        ArgumentNullException.ThrowIfNull(usageSource);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(apiRequestTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cliRequestTimeout, TimeSpan.Zero);

        this._processRunner = processRunner;
        this._cliCommandResolver = cliCommandResolver;
        this._httpClient = httpClient;
        this._apiKeySource = apiKeySource;
        this._usageSource = usageSource;
        this._cliVersionReader = cliVersionReader;
        this._apiRequestTimeout = apiRequestTimeout;
        this._cliRequestTimeout = cliRequestTimeout;
    }

    public ProviderId Id => ProviderId.TheClawBay;

    public string DisplayName => ProviderId.TheClawBay.DisplayName;

    public Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken) => this._usageSource() switch
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
        cancellationToken.ThrowIfCancellationRequested();
        TheClawBayCliCommand? command = this._cliCommandResolver.Resolve();
        cancellationToken.ThrowIfCancellationRequested();
        if (command is null || this._cliVersionReader is null)
        {
            return null;
        }

        return await this._cliVersionReader.ReadAsync(
            CreateCliSpec(command, ["--version"]),
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

        cancellationToken.ThrowIfCancellationRequested();
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
        timeout.CancelAfter(this._apiRequestTimeout);

        try
        {
            using HttpResponseMessage response = await this._httpClient.SendAsync(
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
        cancellationToken.ThrowIfCancellationRequested();
        TheClawBayCliCommand? command = this._cliCommandResolver.Resolve();
        cancellationToken.ThrowIfCancellationRequested();
        if (command is null)
        {
            throw new ProviderException(
                ProviderErrorCategory.NotInstalled,
                "TheClawBay CLI is not installed or is not on PATH.");
        }

        ProcessStartSpec spec = CreateCliSpec(
            command,
            ["usage", "--json"],
            environment: new Dictionary<string, string?>
            {
                ["NO_COLOR"] = "1",
                ["TERM"] = "dumb",
            });
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(this._cliRequestTimeout);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessRunResult result = await this._processRunner.RunAsync(
                spec,
                MaximumResponseBytes,
                MaximumStandardErrorBytes,
                timeout.Token).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                if (IsOfficialMissingCredentialError(result.StandardError))
                {
                    throw new ProviderException(
                        ProviderErrorCategory.AuthenticationRequired,
                        "Run theclawbay setup, then refresh.");
                }

                throw new ProviderException(
                    ProviderErrorCategory.Unavailable,
                    "TheClawBay CLI could not return usage. Run theclawbay usage --json directly, then refresh.");
            }

            if (IsEmptyOrWhiteSpace(result.StandardOutput))
            {
                throw new ProviderException(
                    ProviderErrorCategory.InvalidResponse,
                    "TheClawBay CLI returned an empty usage response.");
            }

            return TheClawBayUsageParser.Parse(
                result.StandardOutput,
                "TheClawBay CLI");
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (ProcessOutputLimitExceededException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "TheClawBay CLI returned a usage response that was too large to process safely.",
                exception);
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

    private static ProcessStartSpec CreateCliSpec(
        TheClawBayCliCommand command,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null) => new(
            command.ExecutablePath,
            [.. command.PrefixArguments, .. arguments],
            Environment: environment);

    private static bool IsEmptyOrWhiteSpace(ReadOnlySpan<byte> output)
    {
        foreach (byte value in output)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOfficialMissingCredentialError(string standardError)
    {
        StringBuilder normalized = new();
        foreach (string rawLine in standardError.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string line = rawLine;
            while (line.StartsWith('»'))
            {
                line = line[1..].TrimStart();
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (normalized.Length > 0)
            {
                normalized.Append(' ');
            }

            normalized.Append(line);
        }

        return string.Equals(
            normalized.ToString(),
            OfficialMissingCredentialError,
            StringComparison.Ordinal);
    }

    private string? ReadApiKey()
    {
        try
        {
            string? apiKey = this._apiKeySource.ReadApiKey()?.Trim();
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
