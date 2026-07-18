using System.Net;
using System.Text.Json;
using UsageDeck.Core.Providers;

namespace UsageDeck.Infrastructure.Providers.Status;

public sealed class StatusPageProvider : IProviderStatusProvider
{
    private const int MaximumResponseBytes = 1_000_000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _httpClient;
    private readonly string[] _incidentKeywords;
    private readonly HashSet<string> _relevantComponents;
    private readonly Uri _summaryEndpoint;

    public StatusPageProvider(
        HttpClient httpClient,
        ProviderId id,
        Uri summaryEndpoint,
        Uri officialStatusUri,
        IEnumerable<string> relevantComponents,
        IEnumerable<string> incidentKeywords)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(summaryEndpoint);
        ArgumentNullException.ThrowIfNull(officialStatusUri);
        ArgumentNullException.ThrowIfNull(relevantComponents);
        ArgumentNullException.ThrowIfNull(incidentKeywords);

        this._httpClient = httpClient;
        this.Id = id;
        this._summaryEndpoint = summaryEndpoint;
        this.OfficialStatusUri = officialStatusUri;
        this._relevantComponents = relevantComponents.ToHashSet(StringComparer.OrdinalIgnoreCase);
        this._incidentKeywords = incidentKeywords.ToArray();
    }

    public ProviderId Id { get; }

    public Uri OfficialStatusUri { get; }

    public async Task<ProviderServiceStatusSnapshot> FetchStatusAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using HttpResponseMessage response = await this._httpClient.GetAsync(
                this._summaryEndpoint,
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
        JsonElement? incident = this.FindRelevantIncident(root);
        string[] degradedComponents = this.FindDegradedComponents(root).ToArray();
        bool hasProblems = incident is not null || degradedComponents.Length > 0;
        string summary = incident is JsonElement relevantIncident
            ? GetRequiredString(relevantIncident, "name")
            : degradedComponents.Length > 0
                ? $"{string.Join(", ", degradedComponents)} reports a service problem."
                : "No problems reported.";
        Uri? incidentUri = incident is JsonElement incidentElement
            ? this.GetIncidentUri(incidentElement)
            : null;

        return new ProviderServiceStatusSnapshot(
            this.Id,
            hasProblems ? ProviderServiceHealth.ProblemsReported : ProviderServiceHealth.Operational,
            summary,
            checkedAt,
            this.OfficialStatusUri,
            incidentUri);
    }

    private IEnumerable<string> FindDegradedComponents(JsonElement root)
    {
        if (!root.TryGetProperty("components", out JsonElement components)
            || components.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The provider status response did not include components.");
        }

        bool foundRelevantComponent = false;
        foreach (JsonElement component in components.EnumerateArray())
        {
            string name = GetRequiredString(component, "name");
            if (!this._relevantComponents.Contains(name))
            {
                continue;
            }

            foundRelevantComponent = true;

            string status = GetRequiredString(component, "status");
            if (!string.Equals(status, "operational", StringComparison.OrdinalIgnoreCase))
            {
                yield return name;
            }
        }

        if (!foundRelevantComponent)
        {
            throw new JsonException("The provider status response did not include the expected service components.");
        }
    }

    private JsonElement? FindRelevantIncident(JsonElement root)
    {
        if (!root.TryGetProperty("incidents", out JsonElement incidents)
            || incidents.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement incident in incidents.EnumerateArray())
        {
            if (this.IncidentAffectsRelevantComponent(incident)
                || this.IncidentNameContainsKeyword(incident))
            {
                return incident;
            }
        }

        return null;
    }

    private bool IncidentAffectsRelevantComponent(JsonElement incident)
    {
        if (!incident.TryGetProperty("components", out JsonElement components)
            || components.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return components.EnumerateArray().Any(component =>
            component.TryGetProperty("name", out JsonElement name)
            && name.ValueKind == JsonValueKind.String
            && this._relevantComponents.Contains(name.GetString()!));
    }

    private bool IncidentNameContainsKeyword(JsonElement incident)
    {
        string name = GetRequiredString(incident, "name");
        return this._incidentKeywords.Any(keyword =>
            name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private Uri? GetIncidentUri(JsonElement incident)
    {
        if (incident.TryGetProperty("shortlink", out JsonElement shortlink)
            && shortlink.ValueKind == JsonValueKind.String
            && Uri.TryCreate(shortlink.GetString(), UriKind.Absolute, out Uri? shortlinkUri))
        {
            return shortlinkUri;
        }

        if (incident.TryGetProperty("id", out JsonElement id)
            && id.ValueKind == JsonValueKind.String)
        {
            return new Uri(this.OfficialStatusUri, $"incidents/{Uri.EscapeDataString(id.GetString()!)}");
        }

        return this.OfficialStatusUri;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"The provider status response did not include '{propertyName}'.");
        }

        return value.GetString()!;
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
}

public sealed class UnavailableProviderStatusProvider(ProviderId id) : IProviderStatusProvider
{
    public ProviderId Id { get; } = id;

    public Uri? OfficialStatusUri => null;

    public Task<ProviderServiceStatusSnapshot> FetchStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProviderServiceStatusSnapshot(
            this.Id,
            ProviderServiceHealth.OfficialStatusUnavailable,
            "No official public status source is available.",
            DateTimeOffset.Now));
    }
}
