using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using UsageDeck.Core.Providers;

[assembly: InternalsVisibleTo("UsageDeck.Infrastructure.Tests")]

namespace UsageDeck.Infrastructure.Providers.Status;

public sealed class TheClawBayStatusProvider(HttpClient httpClient) : IProviderStatusProvider
{
    private const int MaximumResponseBytes = 1_048_576;
    private static readonly Uri StatusSnapshotUri = new("https://theclawbay.com/api/public/status-snapshot");
    private static readonly Uri StatusUri = new("https://theclawbay.com/status");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public ProviderId Id => ProviderId.TheClawBay;

    public Uri OfficialStatusUri => StatusUri;

    public async Task<ProviderServiceStatusSnapshot> FetchStatusAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using HttpResponseMessage response = await this._httpClient.GetAsync(
                StatusSnapshotUri,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            byte[] payload = await ReadBoundedPayloadAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return this.Parse(payload);
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
                "TheClawBay status could not be refreshed.",
                exception);
        }
    }

    internal ProviderServiceStatusSnapshot Parse(ReadOnlySpan<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload.ToArray());
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("TheClawBay status response was not an object.");
        }

        DateTimeOffset fetchedAt = GetRequiredTimestamp(root, "fetchedAt");
        string health = GetRequiredString(root, "health");
        int total = GetRequiredInt32(root, "total");
        int degraded = GetRequiredInt32(root, "degraded");
        if (total < 0 || degraded < 0 || degraded > total)
        {
            throw new JsonException("TheClawBay status response contained invalid aggregate counts.");
        }

        ProviderServiceHealth mapped = health.ToLowerInvariant() switch
        {
            "ok" => ProviderServiceHealth.Operational,
            "warn" or "bad" => ProviderServiceHealth.ProblemsReported,
            _ => throw new JsonException("TheClawBay returned an unsupported aggregate health value."),
        };
        string summary = mapped == ProviderServiceHealth.Operational
            ? "No problems reported."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{degraded} of {total} models report problems.");

        return new ProviderServiceStatusSnapshot(
            this.Id,
            mapped,
            summary,
            fetchedAt,
            this.OfficialStatusUri);
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not string text)
        {
            throw new JsonException($"TheClawBay status response did not include '{propertyName}'.");
        }

        return text;
    }

    private static int GetRequiredInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int number))
        {
            throw new JsonException($"TheClawBay status response did not include a valid '{propertyName}'.");
        }

        return number;
    }

    private static DateTimeOffset GetRequiredTimestamp(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || !value.TryGetDateTimeOffset(out DateTimeOffset timestamp))
        {
            throw new JsonException($"TheClawBay status response did not include a valid '{propertyName}'.");
        }

        return timestamp;
    }

    private static async Task<byte[]> ReadBoundedPayloadAsync(
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
}
