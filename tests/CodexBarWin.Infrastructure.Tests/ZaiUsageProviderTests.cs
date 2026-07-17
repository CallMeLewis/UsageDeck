using System.Net;
using System.Text;
using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Providers.Zai;
using CodexBarWin.Infrastructure.Settings;

namespace CodexBarWin.Infrastructure.Tests;

public sealed class ZaiUsageProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FetchRequiresAKeyBeforeMakingARequest()
    {
        RecordingHandler handler = new(_ => throw new InvalidOperationException("HTTP should not be called."));
        using HttpClient client = new(handler);
        ZaiUsageProvider provider = new(client, new StubApiKeySource(null), () => ZaiApiRegion.Global);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
        Assert.Null(handler.RequestUri);
    }

    [Theory]
    [InlineData(ZaiApiRegion.Global, "https://api.z.ai/api/monitor/usage/quota/limit")]
    [InlineData(ZaiApiRegion.BigModelChina, "https://open.bigmodel.cn/api/monitor/usage/quota/limit")]
    public async Task FetchUsesTheFixedRegionalEndpointAndBearerKey(ZaiApiRegion region, string expectedEndpoint)
    {
        const string Json = """
            {
              "code": 200,
              "success": true,
              "data": {
                "limits": [
                  { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 12 }
                ]
              }
            }
            """;
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Json, Encoding.UTF8, "application/json"),
        });
        using HttpClient client = new(handler);
        ZaiUsageProvider provider = new(
            client,
            new StubApiKeySource("private-api-key"),
            () => region,
            new FixedTimeProvider(Now));

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(expectedEndpoint, handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("private-api-key", handler.AuthorizationParameter);
        Assert.Contains("application/json", handler.AcceptMediaTypes);
        Assert.Equal(Now, snapshot.CapturedAt);
        Assert.Equal(12, Assert.Single(snapshot.UsageWindows).UsedPercent);
    }

    [Fact]
    public async Task FetchMapsUnauthorisedResponseWithoutExposingSecretsOrBody()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("private-api-key server-secret"),
        });
        using HttpClient client = new(handler);
        ZaiUsageProvider provider = new(client, new StubApiKeySource("private-api-key"), () => ZaiApiRegion.Global);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
        Assert.DoesNotContain("private-api-key", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("server-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchRejectsOversizedResponses()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[1_048_577]),
        });
        using HttpClient client = new(handler);
        ZaiUsageProvider provider = new(client, new StubApiKeySource("private-api-key"), () => ZaiApiRegion.Global);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
    }

    private sealed class StubApiKeySource(string? apiKey) : IZaiApiKeySource
    {
        public string? ReadApiKey() => apiKey;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public IReadOnlyList<string> AcceptMediaTypes { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.RequestUri = request.RequestUri;
            this.AuthorizationScheme = request.Headers.Authorization?.Scheme;
            this.AuthorizationParameter = request.Headers.Authorization?.Parameter;
            this.AcceptMediaTypes = request.Headers.Accept
                .Select(value => value.MediaType ?? string.Empty)
                .ToArray();
            return Task.FromResult(responseFactory(request));
        }
    }
}
