using System.Net;
using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers.Status;

namespace UsageDeck.Infrastructure.Tests;

public sealed class StatusPageProviderTests
{
    [Fact]
    public async Task FetchIgnoresUnrelatedProviderProblems()
    {
        StatusPageProvider provider = CreateProvider("""
            {
              "components": [
                { "name": "Codex API", "status": "operational" },
                { "name": "ChatGPT", "status": "major_outage" }
              ],
              "incidents": [
                { "id": "chatgpt-incident", "name": "ChatGPT login unavailable", "components": [] }
              ]
            }
            """);

        ProviderServiceStatusSnapshot snapshot = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.Equal(ProviderServiceHealth.Operational, snapshot.Health);
        Assert.False(snapshot.HasProblems);
    }

    [Fact]
    public async Task FetchReportsARelevantDegradedComponent()
    {
        StatusPageProvider provider = CreateProvider("""
            {
              "components": [
                { "name": "Codex API", "status": "degraded_performance" }
              ],
              "incidents": []
            }
            """);

        ProviderServiceStatusSnapshot snapshot = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.True(snapshot.HasProblems);
        Assert.Contains("Codex API", snapshot.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchUsesARelevantIncidentWithoutComponentMetadata()
    {
        StatusPageProvider provider = CreateProvider("""
            {
              "components": [
                { "name": "Codex API", "status": "operational" }
              ],
              "incidents": [
                { "id": "codex-errors", "name": "Elevated errors in Codex", "components": [] }
              ]
            }
            """);

        ProviderServiceStatusSnapshot snapshot = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.True(snapshot.HasProblems);
        Assert.Equal("Elevated errors in Codex", snapshot.Summary);
        Assert.Equal("https://status.example.com/incidents/codex-errors", snapshot.IncidentUri?.AbsoluteUri);
    }

    [Fact]
    public async Task MissingExpectedComponentsFailsInsteadOfReportingOperational()
    {
        StatusPageProvider provider = CreateProvider("""
            {
              "components": [
                { "name": "ChatGPT", "status": "operational" }
              ],
              "incidents": []
            }
            """);

        ProviderStatusException exception = await Assert.ThrowsAsync<ProviderStatusException>(
            () => provider.FetchStatusAsync(CancellationToken.None));

        Assert.Equal("OpenAI Codex status could not be refreshed.", exception.SafeMessage);
    }

    [Fact]
    public async Task OversizedResponseFailsBeforeItIsParsed()
    {
        StatusPageProvider provider = CreateProvider(new string('x', 1_000_001));

        ProviderStatusException exception = await Assert.ThrowsAsync<ProviderStatusException>(
            () => provider.FetchStatusAsync(CancellationToken.None));

        Assert.Equal("OpenAI Codex status could not be refreshed.", exception.SafeMessage);
    }

    private static StatusPageProvider CreateProvider(string responseBody)
    {
        HttpClient client = new(new StubHandler(responseBody));
        return new StatusPageProvider(
            client,
            ProviderId.Codex,
            new Uri("https://status.example.com/api/v2/summary.json"),
            new Uri("https://status.example.com/"),
            ["Codex API"],
            ["Codex"]);
    }

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });
    }
}
