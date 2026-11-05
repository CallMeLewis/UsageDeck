using System.Net;
using System.Net.Http.Headers;
using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Providers.KimiCode;

namespace UsageDeck.Infrastructure.Tests;

public sealed class KimiCodeUsageProviderTests
{
    private const string UsageJson = """
        { "usages": { "limit_5h": { "used_ratio": 0.4326, "reset_time": "2026-11-05T00:41:27Z" } } }
        """;

    private const string RenewedJson = """
        { "access_token": "renewed", "refresh_token": "renewed-refresh", "expires_in": 900, "scope": "kimi-code", "token_type": "Bearer" }
        """;

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 0, 5, 0, TimeSpan.Zero);
    private static readonly Uri GlobalBase = new("https://api.kimi.ai/coding/v1");
    private static readonly KimiCodeRenewal Renewal = new("refresh", new Uri("https://auth.kimi.ai"), "kimi-code-env-test", "device-123");

    [Fact]
    public async Task FetchSendsTheSignInOnlyToKimiCodesServiceAndReadsThePlan()
    {
        RecordingHandler handler = new(request => request.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)
            ? Json("""{ "user_id": "private", "email": "someone@example.com", "user_level_name": "Pro" }""")
            : Json(UsageJson));
        using HttpClient client = new(handler);
        StubStore store = new(Credentials("token", Now.AddMinutes(10)));
        KimiCodeUsageProvider provider = CreateProvider(client, store);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(
            ["https://api.kimi.ai/coding/v1/usages", "https://api.kimi.ai/coding/v1/me"],
            handler.Requests.Select(request => request.Uri.AbsoluteUri));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer token", request.Authorization);
            Assert.StartsWith("UsageDeck/", request.UserAgent, StringComparison.Ordinal);
            Assert.Contains("application/json", request.Accept);
        });
        Assert.Empty(store.Saved);
        Assert.Equal(UsageDataState.Fresh, snapshot.State);
        Assert.Equal(Now, snapshot.CapturedAt);
        Assert.Equal("Pro", snapshot.Identity?.Plan);
        Assert.Null(snapshot.Identity?.Email);
        Assert.Equal(43.26, Assert.Single(snapshot.UsageWindows).UsedPercent, precision: 6);
    }

    [Fact]
    public async Task ALapsedTokenIsRenewedSavedForKimiCodeAndUsed()
    {
        RecordingHandler handler = new(request => request.Method == HttpMethod.Post ? Json(RenewedJson) : Json(UsageJson));
        using HttpClient client = new(handler);
        StubStore store = new(Credentials("lapsed", Now.AddSeconds(20), Renewal));
        KimiCodeUsageProvider provider = CreateProvider(client, store);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        RecordedRequest renewal = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, renewal.Method);
        Assert.Equal("https://auth.kimi.ai/api/oauth/token", renewal.Uri.AbsoluteUri);
        Assert.Null(renewal.Authorization);
        Assert.Equal("device-123", renewal.DeviceId);
        Assert.StartsWith("UsageDeck/", renewal.UserAgent, StringComparison.Ordinal);
        Assert.Equal(
            $"client_id={KimiCodeUsageProvider.OAuthClientId}&grant_type=refresh_token&refresh_token=refresh",
            renewal.Body);
        (string name, KimiCodeTokenSet tokens) = Assert.Single(store.Saved);
        Assert.Equal("kimi-code-env-test", name);
        Assert.Equal(
            new KimiCodeTokenSet("renewed", "renewed-refresh", Now.ToUnixTimeSeconds() + 900, "kimi-code", "Bearer", 900),
            tokens);
        Assert.Equal(["Bearer renewed", "Bearer renewed"], handler.Requests.Skip(1).Select(request => request.Authorization));
        Assert.Equal(UsageDataState.Fresh, snapshot.State);
    }

    [Fact]
    public async Task ARejectedRenewalAdoptsATokenKimiCodeSavedMeanwhile()
    {
        RecordingHandler handler = new(request => request.Method == HttpMethod.Post
            ? Json("""{ "error": "invalid_grant" }""", HttpStatusCode.BadRequest)
            : Json(UsageJson));
        using HttpClient client = new(handler);
        StubStore store = new(
            Credentials("lapsed", Now.AddSeconds(-5), Renewal),
            Credentials("from-kimi", Now.AddMinutes(15), Renewal with { RefreshToken = "kimi-refresh" }));
        KimiCodeUsageProvider provider = CreateProvider(client, store);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Empty(store.Saved);
        Assert.Equal("Bearer from-kimi", handler.Requests[1].Authorization);
        Assert.Equal(UsageDataState.Fresh, snapshot.State);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{ "error": "invalid_grant" }""")]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    public async Task ARejectedRenewalNeverTouchesKimiCodesSignIn(HttpStatusCode status, string body)
    {
        RecordingHandler handler = new(_ => Json(body, status));
        using HttpClient client = new(handler);
        StubStore store = new(Credentials("lapsed", Now.AddSeconds(-5), Renewal));
        KimiCodeUsageProvider provider = CreateProvider(client, store);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.Unavailable, exception.Category);
        Assert.Contains("kimi login", exception.SafeMessage, StringComparison.Ordinal);
        Assert.Empty(store.Saved);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", ProviderErrorCategory.Transient)]
    [InlineData(HttpStatusCode.OK, """{ "access_token": "a" }""", ProviderErrorCategory.InvalidResponse)]
    [InlineData(HttpStatusCode.BadRequest, """{ "error": "invalid_request" }""", ProviderErrorCategory.Unavailable)]
    public async Task OtherRenewalFailuresAreCategorised(HttpStatusCode status, string body, ProviderErrorCategory expected)
    {
        using HttpClient client = new(new RecordingHandler(_ => Json(body, status)));
        StubStore store = new(Credentials("lapsed", Now.AddSeconds(-5), Renewal));
        KimiCodeUsageProvider provider = CreateProvider(client, store);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(expected, exception.Category);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task AFailedSaveIsReportedPlainly()
    {
        using HttpClient client = new(new RecordingHandler(_ => Json(RenewedJson)));
        StubStore store = new(Credentials("lapsed", Now.AddSeconds(-5), Renewal)) { FailSaves = true };
        KimiCodeUsageProvider provider = CreateProvider(client, store);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.Unavailable, exception.Category);
        Assert.Contains("couldn't save", exception.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALapsedTokenThatCannotBeRenewedIsInactive()
    {
        RecordingHandler handler = new(_ => throw new InvalidOperationException("HTTP should not be called."));
        using HttpClient client = new(handler);
        KimiCodeUsageProvider provider = CreateProvider(client, new StubStore(Credentials("token", Now.AddSeconds(20))));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.Inactive, exception.Category);
        Assert.Equal(KimiCodeUsageProvider.InactiveMessage, exception.SafeMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnApiKeyWithoutExpiryIsUsedDirectly()
    {
        RecordingHandler handler = new(_ => Json(UsageJson));
        using HttpClient client = new(handler);
        KimiCodeUsageProvider provider = CreateProvider(client, new StubStore(Credentials("api-key", expiresAt: null)));

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(UsageDataState.Fresh, snapshot.State);
        Assert.Equal("Bearer api-key", handler.Requests[0].Authorization);
    }

    [Theory]
    [InlineData(false, null, ProviderErrorCategory.NotInstalled)]
    [InlineData(true, null, ProviderErrorCategory.AuthenticationRequired)]
    [InlineData(false, "C:\\tools\\kimi.exe", ProviderErrorCategory.AuthenticationRequired)]
    public async Task MissingSignInDistinguishesNotInstalledFromSignedOut(
        bool hasHome,
        string? executablePath,
        ProviderErrorCategory expected)
    {
        using HttpClient client = new(new RecordingHandler(_ => throw new InvalidOperationException()));
        KimiCodeUsageProvider provider = new(
            client,
            new StubExecutableLocator(executablePath),
            StubStore.SignedOut(hasHome),
            new FixedTimeProvider(Now));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(expected, exception.Category);
    }

    [Fact]
    public async Task ARejectedTokenIsRetriedOnceWhenKimiCodeHasRenewedIt()
    {
        RecordingHandler handler = new(request => request.Headers.Authorization?.Parameter == "old"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : request.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json(UsageJson));
        using HttpClient client = new(handler);
        StubStore store = new(Credentials("old", Now.AddMinutes(10)), Credentials("new", Now.AddMinutes(15)));
        KimiCodeUsageProvider provider = CreateProvider(client, store);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(["Bearer old", "Bearer new", "Bearer new"], handler.Requests.Select(request => request.Authorization));
        Assert.Equal(UsageDataState.Fresh, snapshot.State);
        Assert.Null(snapshot.Identity);
    }

    [Fact]
    public async Task ARejectedTokenThatKimiCodeHasNotRenewedNeedsSignIn()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using HttpClient client = new(handler);
        KimiCodeUsageProvider provider = CreateProvider(client, new StubStore(Credentials("token", Now.AddMinutes(10))));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
        Assert.Contains("kimi login", exception.SafeMessage, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, ProviderErrorCategory.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, ProviderErrorCategory.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, ProviderErrorCategory.Transient)]
    [InlineData(HttpStatusCode.BadRequest, ProviderErrorCategory.Unavailable)]
    public async Task ServiceFailuresAreCategorised(HttpStatusCode status, ProviderErrorCategory expected)
    {
        using HttpClient client = new(new RecordingHandler(_ => new HttpResponseMessage(status)));
        KimiCodeUsageProvider provider = CreateProvider(client, new StubStore(Credentials("token", Now.AddMinutes(10))));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(expected, exception.Category);
    }

    [Fact]
    public async Task ThrottlingPreservesTheRequestedPause()
    {
        using HttpClient client = new(new RecordingHandler(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return response;
        }));
        KimiCodeUsageProvider provider = CreateProvider(client, new StubStore(Credentials("token", Now.AddMinutes(10))));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.Transient, exception.Category);
        Assert.Equal(Now.AddMinutes(10), exception.RetryNotBeforeUtc);
    }

    [Theory]
    [InlineData("""{ "access_token": "a", "refresh_token": "r", "expires_in": "900" }""", true)]
    [InlineData("""{ "access_token": "a", "refresh_token": "r", "expires_in": 0 }""", false)]
    [InlineData("""{ "access_token": "", "refresh_token": "r", "expires_in": 900 }""", false)]
    [InlineData("""{ "access_token": "a", "expires_in": 900 }""", false)]
    [InlineData("not json", false)]
    public void RenewedTokensMustBeComplete(string json, bool valid)
    {
        KimiCodeTokenSet? tokens = KimiCodeUsageProvider.ParseTokenSet(Encoding.UTF8.GetBytes(json), Now);

        Assert.Equal(valid, tokens is not null);
        if (tokens is not null)
        {
            Assert.Equal(new KimiCodeTokenSet("a", "r", Now.ToUnixTimeSeconds() + 900, "", "Bearer", 900), tokens);
        }
    }

    [Fact]
    public async Task ReadsTheCliVersionFromKimi()
    {
        StubCliVersionReader versions = new("2.4.1");
        using HttpClient client = new(new RecordingHandler(_ => throw new InvalidOperationException()));
        KimiCodeUsageProvider provider = new(
            client,
            new StubExecutableLocator("C:\\tools\\kimi.exe"),
            StubStore.SignedOut(hasHome: true),
            cliVersionReader: versions);

        Assert.Equal("2.4.1", await provider.ReadCliVersionAsync(CancellationToken.None));
        Assert.Equal("C:\\tools\\kimi.exe", versions.Spec?.ExecutablePath);
        Assert.Equal(["--version"], versions.Spec?.Arguments);
    }

    private static KimiCodeUsageProvider CreateProvider(HttpClient client, StubStore store) =>
        new(client, new StubExecutableLocator("C:\\tools\\kimi.exe"), store, new FixedTimeProvider(Now));

    private static KimiCodeCredentials Credentials(string token, DateTimeOffset? expiresAt, KimiCodeRenewal? renewal = null) =>
        new(token, expiresAt, GlobalBase) { Renewal = renewal };

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubStore(params KimiCodeCredentials?[] reads) : IKimiCodeCredentialsStore
    {
        private int _index;

        public bool HasKimiCodeHome { get; init; } = true;

        public bool FailSaves { get; init; }

        public List<(string Name, KimiCodeTokenSet Tokens)> Saved { get; } = [];

        public static StubStore SignedOut(bool hasHome) => new([null]) { HasKimiCodeHome = hasHome };

        public KimiCodeCredentials? Read() => reads[Math.Min(this._index++, reads.Length - 1)];

        public void Save(string storageName, KimiCodeTokenSet tokens)
        {
            if (this.FailSaves)
            {
                throw new IOException("The credentials file is locked.");
            }

            this.Saved.Add((storageName, tokens));
        }
    }

    private sealed class StubExecutableLocator(string? path) : IExecutableLocator
    {
        public string? FindExecutable(string executableName) => executableName == "kimi" ? path : null;
    }

    private sealed class StubCliVersionReader(string version) : ICliVersionReader
    {
        public ProcessStartSpec? Spec { get; private set; }

        public Task<string?> ReadAsync(ProcessStartSpec spec, CancellationToken cancellationToken)
        {
            this.Spec = spec;
            return Task.FromResult<string?>(version);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string UserAgent,
        string Accept,
        string? DeviceId,
        string? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.UserAgent.ToString(),
                request.Headers.Accept.ToString(),
                request.Headers.TryGetValues("X-Msh-Device-Id", out IEnumerable<string>? values) ? values.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responseFactory(request);
        }
    }
}
