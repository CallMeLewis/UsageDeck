using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UsageDeck.Core.Providers;

namespace UsageDeck.Infrastructure.Providers.Status;

public sealed partial class AiStudioStatusProvider(HttpClient httpClient) : IProviderStatusProvider
{
    private const int MaximumResponseBytes = 1_000_000;
    private static readonly Uri StatusUri = new("https://aistudio.google.com/status");
    private static readonly Uri IncidentHistoryEndpoint = new(
        "https://alkalimakersuite-pa.clients6.google.com/$rpc/google.internal.alkali.applications.makersuite.v1.MakerSuiteService/ListIncidentsHistory");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public ProviderId Id => ProviderId.Antigravity;

    public Uri OfficialStatusUri => StatusUri;

    public async Task<ProviderServiceStatusSnapshot> FetchStatusAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            string apiKey = await this.FetchPublicApiKeyAsync(timeout.Token).ConfigureAwait(false);
            using HttpRequestMessage request = new(HttpMethod.Post, CreateIncidentHistoryUri(apiKey))
            {
                Content = new StringContent("[]", Encoding.UTF8),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json+protobuf");
            request.Headers.TryAddWithoutValidation("Origin", "https://aistudio.google.com");
            request.Headers.TryAddWithoutValidation("X-User-Agent", "grpc-web-javascript/0.1");

            using HttpResponseMessage response = await this._httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            byte[] payload = await ReadPayloadAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return this.Parse(payload, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or OperationCanceledException
            or IOException
            or JsonException
            or InvalidDataException)
        {
            throw new ProviderStatusException(
                $"{this.Id.DisplayName} status could not be refreshed.",
                exception);
        }
    }

    internal ProviderServiceStatusSnapshot Parse(ReadOnlyMemory<byte> payload, DateTimeOffset checkedAt)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array
            || root.GetArrayLength() != 1
            || root[0].ValueKind != JsonValueKind.Array
            || root[0].GetArrayLength() != 1
            || root[0][0].ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The AI Studio status response did not include incident history.");
        }

        string? activeIncident = root[0][0]
            .EnumerateArray()
            .Select(GetActiveIncidentName)
            .FirstOrDefault(name => name is not null);

        return new ProviderServiceStatusSnapshot(
            this.Id,
            activeIncident is null
                ? ProviderServiceHealth.Operational
                : ProviderServiceHealth.ProblemsReported,
            activeIncident ?? "No problems reported.",
            checkedAt,
            this.OfficialStatusUri,
            activeIncident is null ? null : this.OfficialStatusUri);
    }

    private async Task<string> FetchPublicApiKeyAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await this._httpClient.GetAsync(
            StatusUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        byte[] payload = await ReadPayloadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        string page = Encoding.UTF8.GetString(payload);
        Match match = PublicApiKeyRegex().Match(page);
        if (!match.Success)
        {
            throw new JsonException("The AI Studio status page did not include its public client key.");
        }

        return match.Groups["key"].Value;
    }

    private static string? GetActiveIncidentName(JsonElement incident)
    {
        if (incident.ValueKind != JsonValueKind.Array
            || incident.GetArrayLength() < 4
            || incident[1].ValueKind != JsonValueKind.String
            || incident[3].ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The AI Studio status response contained an invalid incident.");
        }

        JsonElement.ArrayEnumerator updates = incident[3].EnumerateArray();
        JsonElement? latestUpdate = null;
        foreach (JsonElement update in updates)
        {
            latestUpdate = update;
        }

        if (latestUpdate is not JsonElement latest
            || latest.ValueKind != JsonValueKind.Array
            || latest.GetArrayLength() == 0
            || latest[0].ValueKind != JsonValueKind.Number
            || !latest[0].TryGetInt32(out int state))
        {
            throw new JsonException("The AI Studio status response contained an invalid incident update.");
        }

        const int resolvedState = 4;
        return state == resolvedState ? null : incident[1].GetString();
    }

    private static Uri CreateIncidentHistoryUri(string apiKey)
    {
        UriBuilder builder = new(IncidentHistoryEndpoint)
        {
            Query = $"key={Uri.EscapeDataString(apiKey)}",
        };
        return builder.Uri;
    }

    private static async Task<byte[]> ReadPayloadAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("The provider status response was larger than expected.");
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream payload = new();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return payload.ToArray();
            }

            if (payload.Length + bytesRead > MaximumResponseBytes)
            {
                throw new InvalidDataException("The provider status response was larger than expected.");
            }

            await payload.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }
    }

    [GeneratedRegex("\\\"WIu0Nc\\\"\\s*:\\s*\\\"(?<key>AIza[0-9A-Za-z_-]+)\\\"")]
    private static partial Regex PublicApiKeyRegex();
}
