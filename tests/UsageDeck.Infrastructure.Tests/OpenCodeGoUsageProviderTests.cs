using System.Net;
using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers.OpenCodeGo;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.Infrastructure.Tests;

public sealed class OpenCodeGoUsageProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
    private const string EmptyCsv = "billing_source,cost_micro_cents,created_at\n";

    [Theory]
    [InlineData(OpenCodeGoUsageRange.OneDay, "24h")]
    [InlineData(OpenCodeGoUsageRange.SevenDays, "7d")]
    [InlineData(OpenCodeGoUsageRange.ThirtyDays, "30d")]
    public async Task FetchUsesTheFixedUsageEndpointAndServiceKey(
        OpenCodeGoUsageRange range,
        string expectedRange)
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(EmptyCsv, Encoding.UTF8, "text/csv"),
        });
        using HttpClient client = new(handler);
        OpenCodeGoUsageProvider provider = CreateProvider(client, "oc_sk_private", range);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(
            $"https://console.opencode.ai/api/v1/usage/export?scope=organization&range={expectedRange}",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("oc_sk_private", handler.AuthorizationParameter);
        Assert.Contains("text/csv", handler.AcceptMediaTypes);
        Assert.Equal("OpenCode Console API billing", snapshot.SourceDescription);
    }

    [Fact]
    public async Task FetchMapsUnauthorisedResponseWithoutExposingSecretsOrBody()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("oc_sk_private server-secret"),
        });
        using HttpClient client = new(handler);
        OpenCodeGoUsageProvider provider = CreateProvider(
            client,
            "oc_sk_private",
            OpenCodeGoUsageRange.ThirtyDays);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
        Assert.DoesNotContain("oc_sk_private", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("server-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchRejectsOversizedExports()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[(16 * 1024 * 1024) + 1]),
        });
        using HttpClient client = new(handler);
        OpenCodeGoUsageProvider provider = CreateProvider(
            client,
            "oc_sk_private",
            OpenCodeGoUsageRange.ThirtyDays);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
    }

    private static OpenCodeGoUsageProvider CreateProvider(
        HttpClient client,
        string apiKey,
        OpenCodeGoUsageRange range) =>
        new(
            new OpenCodeGoDataLocator(),
            new OpenCodeGoUsageReader(),
            timeProvider: new FixedTimeProvider(Now),
            httpClient: client,
            apiKeySource: new StubApiKeySource(apiKey),
            usageRange: () => range);

    private sealed class StubApiKeySource(string? apiKey) : IOpenCodeGoApiKeySource
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
