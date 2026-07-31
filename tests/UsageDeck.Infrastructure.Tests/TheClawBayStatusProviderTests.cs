using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers.Status;

namespace UsageDeck.Infrastructure.Tests;

public sealed class TheClawBayStatusProviderTests
{
    private const string StatusSnapshotEndpoint = "https://theclawbay.com/api/public/status-snapshot";
    private const string StatusPage = "https://theclawbay.com/status";

    [Theory]
    [InlineData("ok", 42, 0, ProviderServiceHealth.Operational, "No problems reported.")]
    [InlineData("warn", 42, 3, ProviderServiceHealth.ProblemsReported, "3 of 42 models report problems.")]
    [InlineData("bad", 42, 42, ProviderServiceHealth.ProblemsReported, "42 of 42 models report problems.")]
    public void ParseMapsOfficialAggregateHealth(
        string health,
        int total,
        int degraded,
        ProviderServiceHealth expectedHealth,
        string expectedSummary)
    {
        TheClawBayStatusProvider provider = CreateProvider();

        ProviderServiceStatusSnapshot snapshot = provider.Parse(CreatePayload(health, total, degraded));

        Assert.Equal(ProviderId.TheClawBay, snapshot.ProviderId);
        Assert.Equal(expectedHealth, snapshot.Health);
        Assert.Equal(expectedSummary, snapshot.Summary);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-31T17:01:04Z", CultureInfo.InvariantCulture),
            snapshot.CheckedAt);
        Assert.Equal(new Uri(StatusPage), snapshot.OfficialStatusUri);
        Assert.Null(snapshot.IncidentUri);
    }

    [Fact]
    public void ParseAcceptsCaseInsensitiveOfficialHealth()
    {
        TheClawBayStatusProvider provider = CreateProvider();

        ProviderServiceStatusSnapshot snapshot = provider.Parse(CreatePayload("WARN", 9, 1));

        Assert.Equal(ProviderServiceHealth.ProblemsReported, snapshot.Health);
        Assert.Equal("1 of 9 models report problems.", snapshot.Summary);
    }

    [Theory]
    [InlineData("unknown", 1, 0)]
    [InlineData("", 1, 0)]
    public void ParseRejectsUnsupportedAggregateHealth(string health, int total, int degraded)
    {
        TheClawBayStatusProvider provider = CreateProvider();

        Assert.Throws<JsonException>(() => provider.Parse(CreatePayload(health, total, degraded)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"fetchedAt\":\"2026-07-31T17:01:04Z\",\"health\":\"ok\",\"total\":1}")]
    [InlineData("{\"fetchedAt\":\"2026-07-31T17:01:04Z\",\"health\":\"ok\",\"degraded\":0}")]
    [InlineData("{\"health\":\"ok\",\"total\":1,\"degraded\":0}")]
    public void ParseRejectsMissingRequiredFields(string json)
    {
        TheClawBayStatusProvider provider = CreateProvider();

        Assert.Throws<JsonException>(() => provider.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 2)]
    public void ParseRejectsInvalidAggregateCounts(int total, int degraded)
    {
        TheClawBayStatusProvider provider = CreateProvider();

        Assert.Throws<JsonException>(() => provider.Parse(CreatePayload("warn", total, degraded)));
    }

    [Fact]
    public void ParseRejectsMalformedFetchedAt()
    {
        TheClawBayStatusProvider provider = CreateProvider();
        byte[] payload = Encoding.UTF8.GetBytes(
            "{\"fetchedAt\":\"not-a-timestamp\",\"health\":\"ok\",\"total\":1,\"degraded\":0}");

        Assert.Throws<JsonException>(() => provider.Parse(payload));
    }

    [Fact]
    public async Task FetchUsesOnlyTheOfficialAggregateEndpoint()
    {
        RecordingHandler handler = new(_ => Task.FromResult(CreateResponse(CreatePayload("ok", 1, 0))));
        TheClawBayStatusProvider provider = new(new HttpClient(handler));

        ProviderServiceStatusSnapshot snapshot = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.Equal(ProviderServiceHealth.Operational, snapshot.Health);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(StatusSnapshotEndpoint, handler.Requests[0].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task FetchRejectsAnOversizedDeclaredResponse()
    {
        RecordingHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new DeclaredLengthContent(1_048_577),
        }));
        TheClawBayStatusProvider provider = new(new HttpClient(handler));

        ProviderStatusException exception = await Assert.ThrowsAsync<ProviderStatusException>(
            () => provider.FetchStatusAsync(CancellationToken.None));

        Assert.Equal("TheClawBay status could not be refreshed.", exception.SafeMessage);
    }

    [Fact]
    public async Task FetchRejectsAnOversizedStreamedResponse()
    {
        RecordingHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[1_048_577]),
        }));
        TheClawBayStatusProvider provider = new(new HttpClient(handler));

        ProviderStatusException exception = await Assert.ThrowsAsync<ProviderStatusException>(
            () => provider.FetchStatusAsync(CancellationToken.None));

        Assert.Equal("TheClawBay status could not be refreshed.", exception.SafeMessage);
    }

    [Fact]
    public async Task FetchWrapsTransportFailureWithoutLeakingItsDetails()
    {
        RecordingHandler handler = new(_ => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("private-payload")));
        TheClawBayStatusProvider provider = new(new HttpClient(handler));

        ProviderStatusException exception = await Assert.ThrowsAsync<ProviderStatusException>(
            () => provider.FetchStatusAsync(CancellationToken.None));

        Assert.Equal("TheClawBay status could not be refreshed.", exception.SafeMessage);
        Assert.DoesNotContain("private-payload", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchWrapsARequestThatExceedsTheTenSecondTimeout()
    {
        RecordingHandler handler = new(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateResponse(CreatePayload("ok", 1, 0));
        });
        using HttpClient client = new(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        TheClawBayStatusProvider provider = new(client);
        Stopwatch stopwatch = Stopwatch.StartNew();

        ProviderStatusException exception = await Assert.ThrowsAsync<ProviderStatusException>(
            () => provider.FetchStatusAsync(CancellationToken.None));

        Assert.Equal("TheClawBay status could not be refreshed.", exception.SafeMessage);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task FetchPropagatesCallerCancellation()
    {
        RecordingHandler handler = new(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateResponse(CreatePayload("ok", 1, 0));
        });
        TheClawBayStatusProvider provider = new(new HttpClient(handler));
        using CancellationTokenSource cancellation = new();

        Task<ProviderServiceStatusSnapshot> fetch = provider.FetchStatusAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    [Fact]
    public void SourcesRegisterTheClawBayInSupportedOrder()
    {
        IReadOnlyList<IProviderStatusProvider> providers = ProviderStatusSources.Create(
            new HttpClient(new RecordingHandler(_ => Task.FromResult(CreateResponse(CreatePayload("ok", 1, 0))))));

        Assert.Equal(ProviderId.Supported, providers.Select(provider => provider.Id));
        TheClawBayStatusProvider provider = Assert.IsType<TheClawBayStatusProvider>(providers[^1]);
        Assert.Equal(StatusPage, provider.OfficialStatusUri?.AbsoluteUri);
    }

    private static TheClawBayStatusProvider CreateProvider() => new(new HttpClient());

    private static byte[] CreatePayload(string health, int total, int degraded) => Encoding.UTF8.GetBytes($$"""
        {
          "fetchedAt": "2026-07-31T17:01:04Z",
          "health": "{{health}}",
          "total": {{total}},
          "degraded": {{degraded}}
        }
        """);

    private static HttpResponseMessage CreateResponse(byte[] payload) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(payload),
    };

    private sealed class RecordingHandler(
        Func<CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.Requests.Add(new RecordedRequest(request.Method, request.RequestUri!));
            return sendAsync(cancellationToken);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri);

    private sealed class DeclaredLengthContent : HttpContent
    {
        private readonly int _contentLength;

        public DeclaredLengthContent(int contentLength)
        {
            this._contentLength = contentLength;
            this.Headers.ContentLength = contentLength;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = this._contentLength;
            return true;
        }
    }

    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(payload).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new NonSeekableMemoryStream(payload));
    }

    private sealed class NonSeekableMemoryStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _inner = new(payload, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => this._inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            this._inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
