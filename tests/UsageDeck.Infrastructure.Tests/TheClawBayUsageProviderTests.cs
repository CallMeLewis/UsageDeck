using System.Net;
using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Providers.TheClawBay;
using UsageDeck.Infrastructure.Security;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.Infrastructure.Tests;

public sealed class TheClawBayUsageProviderTests
{
    private const string QuotaJson = """
        {
          "observedAt": "2026-07-31T16:00:00Z",
          "usage": {
            "fiveHour": {
              "progressPercentUsed": 27.5,
              "windowEnd": "2026-07-31T20:00:00Z"
            },
            "weekly": {
              "percentUsed": 63,
              "windowEnd": "2026-08-03T00:00:00Z"
            }
          }
        }
        """;

    [Fact]
    public async Task ApiKeyModeUsesOnlyTheFixedQuotaEndpoint()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(QuotaJson, Encoding.UTF8, "application/json"),
        });
        FakeProcessSessionFactory processes = new("unused");
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            processes,
            "private-key",
            TheClawBayUsageSource.ApiKey);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("https://theclawbay.com/api/codex-auth/v1/quota", handler.RequestUri?.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("private-key", handler.AuthorizationParameter);
        Assert.Contains("application/json", handler.AcceptMediaTypes);
        Assert.Equal("TheClawBay API", snapshot.SourceDescription);
        Assert.Null(processes.StartSpec);
    }

    [Fact]
    public async Task ApiKeyModeRequiresAConfiguredKeyWithoutTryingCli()
    {
        RecordingHandler handler = new(_ => throw new InvalidOperationException("HTTP should not be called."));
        FakeProcessSessionFactory processes = new(QuotaJson);
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            processes,
            "  ",
            TheClawBayUsageSource.ApiKey);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
        Assert.Null(handler.RequestUri);
        Assert.Null(processes.StartSpec);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ApiKeyModeMapsRejectedKeysSafely(HttpStatusCode statusCode)
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("private-key server-body-marker"),
        });
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            new FakeProcessSessionFactory("unused"),
            "private-key",
            TheClawBayUsageSource.ApiKey);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
        AssertSafe(exception);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ApiKeyModeMapsTemporaryHttpFailuresSafely(HttpStatusCode statusCode)
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("private-key server-body-marker"),
        });
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            new FakeProcessSessionFactory("unused"),
            "private-key",
            TheClawBayUsageSource.ApiKey);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.Transient, exception.Category);
        AssertSafe(exception);
    }

    [Fact]
    public async Task ApiKeyModeMapsOtherHttpFailuresSafely()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("private-key server-body-marker"),
        });
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            new FakeProcessSessionFactory("unused"),
            "private-key",
            TheClawBayUsageSource.ApiKey);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.Unavailable, exception.Category);
        AssertSafe(exception);
    }

    [Fact]
    public async Task ApiKeyModeMapsRequestTimeoutToTransient()
    {
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(new BlockingHandler()),
            new FakeProcessSessionFactory("unused"),
            "private-key",
            TheClawBayUsageSource.ApiKey);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.Transient, exception.Category);
        AssertSafe(exception);
    }

    [Fact]
    public async Task ApiKeyModePropagatesCallerCancellation()
    {
        using CancellationTokenSource cancellation = new();
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(new BlockingHandler()),
            new FakeProcessSessionFactory("unused"),
            "private-key",
            TheClawBayUsageSource.ApiKey);

        Task<ProviderSnapshot> fetch = provider.FetchAsync(cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    [Fact]
    public async Task ApiKeyModeRejectsOversizedDeclaredContent()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[1_048_577]),
        });
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            new FakeProcessSessionFactory("unused"),
            "private-key",
            TheClawBayUsageSource.ApiKey);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
        AssertSafe(exception);
    }

    [Fact]
    public async Task ApiKeyModeRejectsOversizedStreamedContent()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[1_048_577]),
        });
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            new FakeProcessSessionFactory("unused"),
            "private-key",
            TheClawBayUsageSource.ApiKey);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
        AssertSafe(exception);
    }

    [Fact]
    public async Task ApiKeyModePreservesMalformedJsonAsInvalidResponse()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ \"secret\": \"server-body-marker\""),
        });
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            new FakeProcessSessionFactory("unused"),
            "private-key",
            TheClawBayUsageSource.ApiKey);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
        AssertSafe(exception);
    }

    [Fact]
    public async Task CliModeRunsOnlyTheOfficialJsonCommand()
    {
        FakeProcessSessionFactory processes = new(QuotaJson);
        RecordingHandler handler = new(_ => throw new InvalidOperationException("HTTP should not be called."));
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            processes,
            "private-key",
            TheClawBayUsageSource.Cli);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(["usage", "--json"], processes.StartSpec?.Arguments);
        Assert.DoesNotContain(
            processes.StartSpec?.Arguments ?? [],
            value => value.Contains("private-key", StringComparison.Ordinal));
        Assert.Equal("1", processes.StartSpec?.Environment?["NO_COLOR"]);
        Assert.Equal("dumb", processes.StartSpec?.Environment?["TERM"]);
        Assert.DoesNotContain(
            processes.StartSpec?.Environment?.Values ?? [],
            value => value?.Contains("private-key", StringComparison.Ordinal) is true);
        Assert.Equal("TheClawBay CLI", snapshot.SourceDescription);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task AutomaticUsesApiWithoutStartingCliWhenApiSucceeds()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(QuotaJson, Encoding.UTF8, "application/json"),
        });
        FakeProcessSessionFactory processes = new("unused");
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            processes,
            "private-key",
            TheClawBayUsageSource.Automatic);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("TheClawBay API", snapshot.SourceDescription);
        Assert.NotNull(handler.RequestUri);
        Assert.Null(processes.StartSpec);
    }

    [Fact]
    public async Task AutomaticUsesCliWithoutCallingHttpWhenNoKeyIsConfigured()
    {
        RecordingHandler handler = new(_ => throw new InvalidOperationException("HTTP should not be called."));
        FakeProcessSessionFactory processes = new(QuotaJson);
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            processes,
            null,
            TheClawBayUsageSource.Automatic);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("TheClawBay CLI", snapshot.SourceDescription);
        Assert.Null(handler.RequestUri);
        Assert.Equal(["usage", "--json"], processes.StartSpec?.Arguments);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task AutomaticFallsBackToCliForEligibleApiFailures(HttpStatusCode statusCode)
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(statusCode));
        FakeProcessSessionFactory processes = new(QuotaJson);
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            processes,
            "private-key",
            TheClawBayUsageSource.Automatic);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("TheClawBay CLI", snapshot.SourceDescription);
        Assert.Equal(["usage", "--json"], processes.StartSpec?.Arguments);
    }

    [Fact]
    public async Task AutomaticDoesNotResolveOrStartCliWhenApiFailureArrivesAfterCallerCancellation()
    {
        using CancellationTokenSource cancellation = new();
        RecordingHandler handler = new(_ =>
        {
            cancellation.Cancel();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        FakeProcessSessionFactory processes = new(QuotaJson);
        CountingTheClawBayCliCommandResolver commandResolver = new(@"C:\Tools\theclawbay.exe");
        TheClawBayUsageProvider provider = new(
            processes,
            commandResolver,
            new HttpClient(handler),
            new StubApiKeySource("private-key"),
            () => TheClawBayUsageSource.Automatic);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.FetchAsync(cancellation.Token));

        Assert.Equal(0, commandResolver.CallCount);
        Assert.Null(processes.StartSpec);
    }

    [Fact]
    public async Task AutomaticDoesNotMaskApiContractFailuresWithCliFallback()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ \"secret\": \"server-body-marker\""),
        });
        FakeProcessSessionFactory processes = new(QuotaJson);
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            processes,
            "private-key",
            TheClawBayUsageSource.Automatic);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
        Assert.Equal("TheClawBay returned usage data that UsageDeck could not read.", exception.SafeMessage);
        Assert.Null(processes.StartSpec);
        AssertSafe(exception);
    }

    [Fact]
    public async Task AutomaticReportsBothSetupOptionsWhenNoKeyOrCliExists()
    {
        RecordingHandler handler = new(_ => throw new InvalidOperationException("HTTP should not be called."));
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            new FakeProcessSessionFactory("unused"),
            null,
            TheClawBayUsageSource.Automatic,
            executablePath: null);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.NotInstalled, exception.Category);
        Assert.Equal(
            "No TheClawBay API key is configured and TheClawBay CLI was not found. Configure either source in Settings.",
            exception.SafeMessage);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task AutomaticCombinesEligibleFailuresUsingDeterministicCategoryPrecedence()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            new FakeProcessSessionFactory("unused"),
            "private-key",
            TheClawBayUsageSource.Automatic,
            executablePath: null);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.Transient, exception.Category);
        Assert.Equal(
            "No usable TheClawBay source was available. Check the API key or run theclawbay setup, then refresh.",
            exception.SafeMessage);
    }

    [Fact]
    public async Task ExplicitCliModeNeverFallsBackToApi()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(QuotaJson),
        });
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            new FakeProcessSessionFactory("unused"),
            "private-key",
            TheClawBayUsageSource.Cli,
            executablePath: null);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.NotInstalled, exception.Category);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task ExplicitApiModeNeverFallsBackToCliAfterFailure()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        FakeProcessSessionFactory processes = new(QuotaJson);
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(handler),
            processes,
            "private-key",
            TheClawBayUsageSource.ApiKey);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
        Assert.Null(processes.StartSpec);
    }

    [Fact]
    public async Task CliModeMapsTheOfficialMissingCredentialExitToAuthenticationRequired()
    {
        FixedProcessRunner processes = new(new ProcessRunResult(
            [],
            2,
            " » Error: No saved credential found. Run \"theclawbay setup\" or pass \r\n » --api-key."));
        TheClawBayUsageProvider provider = new(
            processes,
            new FixedTheClawBayCliCommandResolver(
                new TheClawBayCliCommand(@"C:\Tools\theclawbay.exe", [])),
            new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException())),
            new StubApiKeySource(null),
            () => TheClawBayUsageSource.Cli);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
        Assert.Equal("Run theclawbay setup, then refresh.", exception.SafeMessage);
        Assert.DoesNotContain("--api-key", exception.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliModeMapsUnknownNonZeroExitToUnavailableWithoutExposingStandardError()
    {
        FixedProcessRunner processes = new(new ProcessRunResult(
            [],
            7,
            "private-key server-body-marker"));
        TheClawBayUsageProvider provider = new(
            processes,
            new FixedTheClawBayCliCommandResolver(
                new TheClawBayCliCommand(@"C:\Tools\theclawbay.exe", [])),
            new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException())),
            new StubApiKeySource(null),
            () => TheClawBayUsageSource.Cli);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.Unavailable, exception.Category);
        AssertSafe(exception);
    }

    [Fact]
    public async Task CliModeRejectsParseableOutputFromANonZeroExit()
    {
        FixedProcessRunner processes = new(new ProcessRunResult(
            Encoding.UTF8.GetBytes(QuotaJson),
            1,
            "server-body-marker"));
        TheClawBayUsageProvider provider = new(
            processes,
            new FixedTheClawBayCliCommandResolver(
                new TheClawBayCliCommand(@"C:\Tools\theclawbay.exe", [])),
            new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException())),
            new StubApiKeySource(null),
            () => TheClawBayUsageSource.Cli);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.Unavailable, exception.Category);
        AssertSafe(exception);
    }

    [Fact]
    public async Task CliModeMapsEmptySuccessfulOutputToInvalidResponse()
    {
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException())),
            new FakeProcessSessionFactory(string.Empty),
            "private-key",
            TheClawBayUsageSource.Cli);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
        Assert.Equal("TheClawBay CLI returned an empty usage response.", exception.SafeMessage);
    }

    [Fact]
    public async Task CliModeMapsWhitespaceOnlySuccessfulOutputToInvalidResponse()
    {
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException())),
            new FakeProcessSessionFactory(" \r\n\t"),
            "private-key",
            TheClawBayUsageSource.Cli);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
        Assert.Equal("TheClawBay CLI returned an empty usage response.", exception.SafeMessage);
    }

    [Fact]
    public async Task CliModeRejectsOversizedOutput()
    {
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException())),
            new FakeProcessSessionFactory(new string('x', 1_048_577)),
            "private-key",
            TheClawBayUsageSource.Cli);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
        Assert.DoesNotContain(new string('x', 128), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticDoesNotMaskCliContractFailuresWhenNoApiKeyExists()
    {
        TheClawBayUsageProvider provider = CreateProvider(
            new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException())),
            new FakeProcessSessionFactory("{ \"secret\": \"server-body-marker\""),
            null,
            TheClawBayUsageSource.Automatic);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
        Assert.Equal("TheClawBay returned usage data that UsageDeck could not read.", exception.SafeMessage);
        AssertSafe(exception);
    }

    [Fact]
    public async Task CliModeMapsTimeoutToTransient()
    {
        TheClawBayUsageProvider provider = new(
            new BlockingProcessSessionFactory(),
            new FixedTheClawBayCliCommandResolver(
                new TheClawBayCliCommand(@"C:\Tools\theclawbay.exe", [])),
            new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException())),
            new StubApiKeySource("private-key"),
            () => TheClawBayUsageSource.Cli);

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.Transient, exception.Category);
    }

    [Fact]
    public async Task CliModePropagatesCallerCancellation()
    {
        using CancellationTokenSource cancellation = new();
        TheClawBayUsageProvider provider = new(
            new BlockingProcessSessionFactory(),
            new FixedTheClawBayCliCommandResolver(
                new TheClawBayCliCommand(@"C:\Tools\theclawbay.exe", [])),
            new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException())),
            new StubApiKeySource("private-key"),
            () => TheClawBayUsageSource.Cli);

        Task<ProviderSnapshot> fetch = provider.FetchAsync(cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    [Fact]
    public async Task ReadCliVersionAsyncRunsSupportedVersionCommand()
    {
        StubCliVersionReader versionReader = new("1.2.3");
        TheClawBayUsageProvider provider = new(
            new FakeProcessSessionFactory("unused"),
            new FixedTheClawBayCliCommandResolver(
                new TheClawBayCliCommand(@"C:\Tools\theclawbay.exe", [])),
            new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException())),
            new StubApiKeySource(null),
            () => TheClawBayUsageSource.Automatic,
            versionReader);

        string? version = await provider.ReadCliVersionAsync(CancellationToken.None);

        Assert.Equal("1.2.3", version);
        Assert.Equal(@"C:\Tools\theclawbay.exe", versionReader.Spec?.ExecutablePath);
        Assert.Equal(["--version"], versionReader.Spec?.Arguments);
    }

    [Fact]
    public async Task AutomaticFallsBackToCliWhenKeyStorageIsUnavailable()
    {
        TheClawBayUsageProvider provider = new(
            new FakeProcessSessionFactory(QuotaJson),
            new FixedTheClawBayCliCommandResolver(
                new TheClawBayCliCommand(@"C:\Tools\theclawbay.exe", [])),
            new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException("HTTP should not be called."))),
            new ThrowingApiKeySource(),
            () => TheClawBayUsageSource.Automatic);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("TheClawBay CLI", snapshot.SourceDescription);
    }

    private static void AssertSafe(ProviderException exception)
    {
        Assert.DoesNotContain("private-key", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("server-body-marker", exception.Message, StringComparison.Ordinal);
    }

    private static TheClawBayUsageProvider CreateProvider(
        HttpClient client,
        FakeProcessSessionFactory processes,
        string? apiKey,
        TheClawBayUsageSource source,
        string? executablePath = @"C:\Tools\theclawbay.exe") => new(
            processes,
            new FixedTheClawBayCliCommandResolver(executablePath is null
                ? null
                : new TheClawBayCliCommand(executablePath, [])),
            client,
            new StubApiKeySource(apiKey),
            () => source);

    private sealed class StubApiKeySource(string? apiKey) : ITheClawBayApiKeySource
    {
        public string? ReadApiKey() => apiKey;
    }

    private sealed class ThrowingApiKeySource : ITheClawBayApiKeySource
    {
        public string? ReadApiKey() => throw new SecretStoreException(
            "The saved TheClawBay key could not be read safely.",
            new InvalidOperationException("private-key"));
    }

    private sealed class CountingTheClawBayCliCommandResolver(string? path)
        : ITheClawBayCliCommandResolver
    {
        public int CallCount { get; private set; }

        public TheClawBayCliCommand? Resolve()
        {
            this.CallCount++;
            return path is null ? null : new TheClawBayCliCommand(path, []);
        }
    }

    private sealed class FakeProcessSessionFactory(string output) : IBoundedProcessRunner
    {
        public ProcessStartSpec? StartSpec { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            ProcessStartSpec spec,
            int maximumStandardOutputBytes,
            int maximumStandardErrorBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.StartSpec = spec;
            byte[] standardOutput = Encoding.UTF8.GetBytes(output);
            if (standardOutput.Length > maximumStandardOutputBytes)
            {
                throw new ProcessOutputLimitExceededException(maximumStandardOutputBytes);
            }

            return Task.FromResult(new ProcessRunResult(standardOutput, 0, string.Empty));
        }
    }

    private sealed class BlockingProcessSessionFactory : IBoundedProcessRunner
    {
        public async Task<ProcessRunResult> RunAsync(
            ProcessStartSpec spec,
            int maximumStandardOutputBytes,
            int maximumStandardErrorBytes,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class StubCliVersionReader(string? version) : ICliVersionReader
    {
        public ProcessStartSpec? Spec { get; private set; }

        public Task<string?> ReadAsync(ProcessStartSpec spec, CancellationToken cancellationToken)
        {
            this.Spec = spec;
            return Task.FromResult(version);
        }
    }

    private sealed class FixedProcessRunner(ProcessRunResult result) : IBoundedProcessRunner
    {
        public ProcessStartSpec? StartSpec { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            ProcessStartSpec spec,
            int maximumStandardOutputBytes,
            int maximumStandardErrorBytes,
            CancellationToken cancellationToken)
        {
            this.StartSpec = spec;
            return Task.FromResult(result);
        }
    }

    private sealed class FixedTheClawBayCliCommandResolver(TheClawBayCliCommand? command)
        : ITheClawBayCliCommandResolver
    {
        public TheClawBayCliCommand? Resolve() => command;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public int CallCount { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public IReadOnlyList<string> AcceptMediaTypes { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.RequestUri = request.RequestUri;
            this.Method = request.Method;
            this.CallCount++;
            this.AuthorizationScheme = request.Headers.Authorization?.Scheme;
            this.AuthorizationParameter = request.Headers.Authorization?.Parameter;
            this.AcceptMediaTypes = request.Headers.Accept
                .Select(value => value.MediaType ?? string.Empty)
                .ToArray();
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class UnknownLengthContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(content, writable: false));
    }
}
