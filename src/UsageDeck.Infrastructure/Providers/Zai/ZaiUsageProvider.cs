using System.Net;
using System.Net.Http.Headers;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Security;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.Infrastructure.Providers.Zai;

public sealed class ZaiUsageProvider(
    HttpClient httpClient,
    IZaiApiKeySource apiKeySource,
    Func<ZaiApiRegion> region,
    TimeProvider? timeProvider = null) : IUsageProvider
{
    private const int MaximumResponseBytes = 1_048_576;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ProviderId Id => ProviderId.Zai;

    public string DisplayName => ProviderId.Zai.DisplayName;

    public static Uri EndpointFor(ZaiApiRegion region) => region switch
    {
        ZaiApiRegion.Global => new("https://api.z.ai/api/monitor/usage/quota/limit"),
        ZaiApiRegion.BigModelChina => new("https://open.bigmodel.cn/api/monitor/usage/quota/limit"),
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "Unsupported Z.AI API region."),
    };

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        string? apiKey;
        try
        {
            apiKey = apiKeySource.ReadApiKey()?.Trim();
        }
        catch (SecretStoreException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                exception.SafeMessage,
                exception);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ProviderException(
                ProviderErrorCategory.AuthenticationRequired,
                "Add a Z.AI API key in Settings, then refresh.");
        }

        ZaiApiRegion selectedRegion = region();
        Uri endpoint = EndpointFor(selectedRegion);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using HttpResponseMessage response = await SendWithCompatibleAuthenticationAsync(
                httpClient,
                endpoint,
                apiKey,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ProviderException(
                    ProviderErrorCategory.AuthenticationRequired,
                    $"Z.AI rejected the API key for the {RegionName(selectedRegion)} endpoint. Check the key and region in Settings, then refresh.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new ProviderException(
                    ProviderErrorCategory.Transient,
                    "Z.AI Coding Plan usage is temporarily unavailable.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderException(
                    ProviderErrorCategory.Unavailable,
                    "Z.AI could not return Coding Plan usage right now.");
            }

            byte[] body = await ReadBoundedResponseAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return ZaiUsageParser.Parse(body, this._timeProvider.GetUtcNow());
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
                "Z.AI did not return Coding Plan usage in time.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Transient,
                "Z.AI Coding Plan usage could not be reached.",
                exception);
        }
        catch (IOException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Transient,
                "Z.AI Coding Plan usage could not be read.",
                exception);
        }
    }

    private static async Task<HttpResponseMessage> SendWithCompatibleAuthenticationAsync(
        HttpClient httpClient,
        Uri endpoint,
        string apiKey,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await SendAsync(
            httpClient,
            endpoint,
            apiKey,
            useBearerScheme: false,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return response;
        }

        response.Dispose();
        return await SendAsync(
            httpClient,
            endpoint,
            apiKey,
            useBearerScheme: true,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        Uri endpoint,
        string apiKey,
        bool useBearerScheme,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
        if (useBearerScheme)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Authorization", apiKey);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static string RegionName(ZaiApiRegion region) => region switch
    {
        ZaiApiRegion.Global => "Global",
        ZaiApiRegion.BigModelChina => "BigModel China",
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "Unsupported Z.AI API region."),
    };

    private static async Task<byte[]> ReadBoundedResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        long? length = content.Headers.ContentLength;
        if (length > MaximumResponseBytes)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Z.AI returned a usage response that was too large to process safely.");
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
                    "Z.AI returned a usage response that was too large to process safely.");
            }

            result.Write(buffer, 0, read);
        }

        return result.ToArray();
    }
}
