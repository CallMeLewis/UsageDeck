using System.Net;
using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers.Status;

namespace UsageDeck.Infrastructure.Tests;

public sealed class AiStudioStatusProviderTests
{
    private const string StatusPage = """
        <html><script>window.WIZ_global_data = {"WIu0Nc":"AIzaPublicClientKey"};</script></html>
        """;

    [Fact]
    public async Task FetchReportsOperationalWhenAllIncidentsAreResolved()
    {
        RecordingHandler handler = new(StatusPage, """
            [[[["resolved-id","Resolved incident",1,[[1,"2026-07-14",["1784083560"],"Investigating."],[4,"2026-07-15",["1784129940"],"Resolved."]],1,[1]]]]]
            """);
        AiStudioStatusProvider provider = new(new HttpClient(handler));

        ProviderServiceStatusSnapshot snapshot = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.Equal(ProviderId.Antigravity, snapshot.ProviderId);
        Assert.Equal(ProviderServiceHealth.Operational, snapshot.Health);
        Assert.Equal("No problems reported.", snapshot.Summary);
        Assert.Equal("https://aistudio.google.com/status", snapshot.OfficialStatusUri?.AbsoluteUri);
        Assert.Null(snapshot.IncidentUri);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("https://aistudio.google.com/status", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal(
            "https://alkalimakersuite-pa.clients6.google.com/$rpc/google.internal.alkali.applications.makersuite.v1.MakerSuiteService/ListIncidentsHistory?key=AIzaPublicClientKey",
            handler.Requests[1].Uri.AbsoluteUri);
        Assert.Equal("https://aistudio.google.com", handler.Requests[1].Origin);
        Assert.Equal("[]", handler.Requests[1].Body);
    }

    [Fact]
    public async Task FetchReportsTheFirstUnresolvedIncident()
    {
        RecordingHandler handler = new(StatusPage, """
            [[[
                ["resolved-id","Resolved incident",1,[[4,"2026-07-15",["1784129940"],"Resolved."]],1,[1]],
                ["active-id","Gemini API is experiencing increased errors",2,[[1,"2026-07-18",["1784386800"],"Investigation is underway."]],1,[1]]
            ]]]
            """);
        AiStudioStatusProvider provider = new(new HttpClient(handler));

        ProviderServiceStatusSnapshot snapshot = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.Equal(ProviderServiceHealth.ProblemsReported, snapshot.Health);
        Assert.Equal("Gemini API is experiencing increased errors", snapshot.Summary);
        Assert.Equal("https://aistudio.google.com/status", snapshot.IncidentUri?.AbsoluteUri);
    }

    [Fact]
    public async Task MissingPublicClientKeyFailsInsteadOfReportingOperational()
    {
        RecordingHandler handler = new("<html></html>", "[]");
        AiStudioStatusProvider provider = new(new HttpClient(handler));

        ProviderStatusException exception = await Assert.ThrowsAsync<ProviderStatusException>(
            () => provider.FetchStatusAsync(CancellationToken.None));

        Assert.Equal("Antigravity status could not be refreshed.", exception.SafeMessage);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void SourcesUseTheOfficialAiStudioStatusPageForAntigravity()
    {
        IProviderStatusProvider provider = Assert.Single(
            ProviderStatusSources.Create(new HttpClient(new RecordingHandler(StatusPage, "[[]]"))),
            provider => provider.Id == ProviderId.Antigravity);

        Assert.IsType<AiStudioStatusProvider>(provider);
        Assert.Equal("https://aistudio.google.com/status", provider.OfficialStatusUri?.AbsoluteUri);
    }

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private int _responseIndex;

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            this.Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.TryGetValues("Origin", out IEnumerable<string>? origins)
                    ? origins.Single()
                    : null,
                body));

            string response = responses[this._responseIndex++];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Origin, string? Body);
}
