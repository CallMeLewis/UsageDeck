using System.Net;
using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Providers.Claude;

namespace UsageDeck.Infrastructure.Tests;

public sealed class ClaudeUsageProviderTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private const string ApiResponse = """
        {
          "limits": [
            { "kind": "session", "percent": 29, "resets_at": "2026-07-26T02:40:00+01:00" },
            { "kind": "weekly_all", "percent": 3, "resets_at": "2026-07-29T04:00:00+01:00" },
            {
              "kind": "weekly_scoped",
              "percent": 2,
              "resets_at": "2026-07-29T04:00:00+01:00",
              "scope": { "model": { "display_name": "Fable" } }
            }
          ]
        }
        """;

    [Fact]
    public async Task FetchUsesTheUsageApiWithoutStartingAClaudeSession()
    {
        FakePtySessionFactory sessions = new(new FakePtySession("unused"));
        StubHttpHandler handler = new(HttpStatusCode.OK, ApiResponse);
        ClaudeUsageProvider provider = new(
            sessions,
            new StubExecutableLocator("C:\\tools\\claude.exe"),
            new ImmediateTimeProvider(TestNow),
            httpClient: new HttpClient(handler),
            credentialsReader: new StubCredentialsReader(
                new ClaudeCredentials("token-value", TestNow.AddHours(8))));

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("Claude API", snapshot.SourceDescription);
        Assert.Equal(
            ["session", "weekly", "weekly-fable"],
            snapshot.UsageWindows.Select(window => window.Id));
        Assert.Null(sessions.StartSpec);
        Assert.Equal("Bearer token-value", handler.AuthorizationHeader);
        Assert.Equal("oauth-2025-04-20", handler.AnthropicBetaHeader);
    }

    [Fact]
    public async Task FetchFallsBackToTheCliWhenTheApiRejectsTheToken()
    {
        FakePtySessionFactory sessions = new(new FakePtySession("""
            Current session
            25% used
            Resets 4pm
            """));
        ClaudeUsageProvider provider = new(
            sessions,
            new StubExecutableLocator("C:\\tools\\claude.exe"),
            new ImmediateTimeProvider(TestNow),
            httpClient: new HttpClient(new StubHttpHandler(HttpStatusCode.Unauthorized, "{}")),
            credentialsReader: new StubCredentialsReader(
                new ClaudeCredentials("token-value", TestNow.AddHours(8))));

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("Claude CLI", snapshot.SourceDescription);
        Assert.Equal(25, Assert.Single(snapshot.UsageWindows).UsedPercent);
        Assert.NotNull(sessions.StartSpec);
    }

    [Fact]
    public async Task FetchStopsReadingAnOversizedApiResponseAndFallsBackToTheCli()
    {
        TrackingContent content = new(new byte[2_097_152]);
        FakePtySessionFactory sessions = new(new FakePtySession("""
            Current session
            25% used
            Resets 4pm
            """));
        ClaudeUsageProvider provider = new(
            sessions,
            new StubExecutableLocator("C:\\tools\\claude.exe"),
            new ImmediateTimeProvider(TestNow),
            httpClient: new HttpClient(new StubContentHttpHandler(content)),
            credentialsReader: new StubCredentialsReader(
                new ClaudeCredentials("token-value", TestNow.AddHours(8))));

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("Claude CLI", snapshot.SourceDescription);
        Assert.InRange(content.BytesRead, 1, 1_056_768);
    }

    [Fact]
    public async Task FetchSkipsTheApiWhenTheStoredTokenHasExpired()
    {
        FakePtySessionFactory sessions = new(new FakePtySession("""
            Current session
            25% used
            Resets 4pm
            """));
        StubHttpHandler handler = new(HttpStatusCode.OK, ApiResponse);
        ClaudeUsageProvider provider = new(
            sessions,
            new StubExecutableLocator("C:\\tools\\claude.exe"),
            new ImmediateTimeProvider(TestNow),
            httpClient: new HttpClient(handler),
            credentialsReader: new StubCredentialsReader(
                new ClaudeCredentials("token-value", TestNow.AddMinutes(-5))));

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("Claude CLI", snapshot.SourceDescription);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchFallsBackToTheCliWhenNoCredentialsAreStored()
    {
        FakePtySessionFactory sessions = new(new FakePtySession("""
            Current session
            25% used
            Resets 4pm
            """));
        ClaudeUsageProvider provider = new(
            sessions,
            new StubExecutableLocator("C:\\tools\\claude.exe"),
            new ImmediateTimeProvider(TestNow),
            httpClient: new HttpClient(new StubHttpHandler(HttpStatusCode.OK, ApiResponse)),
            credentialsReader: new StubCredentialsReader(null));

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("Claude CLI", snapshot.SourceDescription);
    }
    [Fact]
    public async Task FetchReturnsParsedSnapshotAndCleansUpPty()
    {
        FakePtySession session = new("""
            usage limits
            Current session
            25% used
            Resets 4pm
            """);
        FakePtySessionFactory sessions = new(session);
        ClaudeUsageProvider provider = new(
            sessions,
            new StubExecutableLocator("C:\\tools\\claude.exe"),
            new ImmediateTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero)));

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderId.Claude, snapshot.ProviderId);
        Assert.Equal("Claude CLI", snapshot.SourceDescription);
        Assert.Equal(25, Assert.Single(snapshot.UsageWindows).UsedPercent);
        Assert.True(session.WasKilled);
        Assert.True(session.WasDisposed);
        Assert.Contains("/usage", session.WrittenText, StringComparison.Ordinal);
        Assert.Equal("C:\\tools\\claude.exe", sessions.StartSpec?.ExecutablePath);
        Assert.Contains("--permission-mode", sessions.StartSpec?.Arguments ?? []);
    }

    [Fact]
    public async Task FetchWaitsForPanelRowsThatPaintAfterTheFirstLimit()
    {
        // Claude Code paints the per-model weekly row after the rest of the panel, so a capture
        // that stops a fixed interval after the first row appears loses it entirely.
        ScriptedPtySession session = new();
        ScriptedTimeProvider time = new(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero), session);

        // Offsets run from the provider starting; it spends the first ~4.15s waiting for the
        // prompt to render before it sends /usage.
        time.Schedule(TimeSpan.FromSeconds(4.3), "Current session\n3% used (Resets 2:40am)\n");
        time.Schedule(TimeSpan.FromSeconds(6), "Current week (Fable)\n1% used (Resets Jul 29, 4am)\n");

        ClaudeUsageProvider provider = new(
            new FakePtySessionFactory(session),
            new StubExecutableLocator("C:\\tools\\claude.exe"),
            time);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.False(session.ReleaseTimedOut);
        Assert.Equal(["session", "weekly-fable"], snapshot.UsageWindows.Select(window => window.Id));
    }

    [Fact]
    public async Task FetchSettlesOnAPanelThatRenderedWithoutLiteralSpaces()
    {
        // Some frames are padded with cursor movement instead of spaces, so the settle loop has
        // to recognise "Currentsession" too - otherwise it waits out its whole budget on a panel
        // that finished painting seconds ago.
        ScriptedPtySession session = new();
        ScriptedTimeProvider time = new(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero), session);
        time.Schedule(
            TimeSpan.FromSeconds(4.3),
            "Currentsession\n██████████    20% usedResets2:40am(Europe/London)");

        ClaudeUsageProvider provider = new(
            new FakePtySessionFactory(session),
            new StubExecutableLocator("C:\\tools\\claude.exe"),
            time);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(20, Assert.Single(snapshot.UsageWindows).UsedPercent);
        Assert.True(
            time.Elapsed < TimeSpan.FromSeconds(8),
            $"Expected the capture to settle shortly after the panel painted, but it took {time.Elapsed}.");
    }

    private sealed class StubExecutableLocator(string path) : IExecutableLocator
    {
        public string? FindExecutable(string executableName) => path;
    }

    private sealed class StubCredentialsReader(ClaudeCredentials? credentials) : IClaudeCredentialsReader
    {
        public ClaudeCredentials? Read() => credentials;
    }

    private sealed class StubHttpHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public string? AuthorizationHeader { get; private set; }

        public string? AnthropicBetaHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.RequestCount++;
            this.AuthorizationHeader = request.Headers.TryGetValues("Authorization", out IEnumerable<string>? auth)
                ? string.Join(" ", auth)
                : null;
            this.AnthropicBetaHeader = request.Headers.TryGetValues("anthropic-beta", out IEnumerable<string>? beta)
                ? string.Join(" ", beta)
                : null;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body),
            });
        }
    }

    private sealed class StubContentHttpHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class TrackingContent(byte[] payload) : HttpContent
    {
        private TrackingStream? _stream;
        private int _bytesSerialised;

        public int BytesRead => Math.Max(this._stream?.BytesRead ?? 0, this._bytesSerialised);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(payload);
            this._bytesSerialised = payload.Length;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            this._stream = new TrackingStream(payload);
            return Task.FromResult<Stream>(this._stream);
        }
    }

    private sealed class TrackingStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _inner = new(payload, writable: false);

        public int BytesRead { get; private set; }

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

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = this._inner.Read(buffer, offset, count);
            this.BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int read = await this._inner.ReadAsync(buffer, cancellationToken);
            this.BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakePtySessionFactory(IPtySession session) : IPtySessionFactory
    {
        public PtyStartSpec? StartSpec { get; private set; }

        public Task<IPtySession> StartAsync(PtyStartSpec spec, CancellationToken cancellationToken)
        {
            this.StartSpec = spec;
            return Task.FromResult<IPtySession>(session);
        }
    }

    private sealed class FakePtySession(string output) : IPtySession
    {
        private readonly byte[] _output = Encoding.UTF8.GetBytes(output);
        private bool _hasReadOutput;
        private readonly StringBuilder _written = new();

        public bool WasKilled { get; private set; }

        public bool WasDisposed { get; private set; }

        public string WrittenText => this._written.ToString();

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (!this._hasReadOutput)
            {
                this._hasReadOutput = true;
                this._output.CopyTo(buffer);
                return this._output.Length;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            this._written.Append(Encoding.UTF8.GetString(buffer.Span));
            return Task.CompletedTask;
        }

        public void Kill() => this.WasKilled = true;

        public ValueTask DisposeAsync()
        {
            this.WasDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    // Emits output on demand rather than all at once, so a test can model a panel that paints
    // in stages. Release blocks until the capture loop has appended the chunk, which keeps the
    // provider's settle loop from racing the reader.
    private sealed class ScriptedPtySession : IPtySession
    {
        private readonly Queue<byte[]> _pending = new();
        private readonly Lock _gate = new();
        private TaskCompletionSource _available = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _consumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _hasOutstandingChunk;

        public bool ReleaseTimedOut { get; private set; }

        public void Release(string text)
        {
            Task consumed;
            lock (this._gate)
            {
                this._consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                consumed = this._consumed.Task;
                this._pending.Enqueue(Encoding.UTF8.GetBytes(text));
                this._available.TrySetResult();
            }

            this.ReleaseTimedOut |= !consumed.Wait(TimeSpan.FromSeconds(10));
        }

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            while (true)
            {
                byte[]? chunk;
                Task available;
                lock (this._gate)
                {
                    if (this._hasOutstandingChunk)
                    {
                        // The capture loop only asks for more once it has appended the last read.
                        this._hasOutstandingChunk = false;
                        this._consumed.TrySetResult();
                    }

                    this._pending.TryDequeue(out chunk);
                    if (chunk is null)
                    {
                        if (this._available.Task.IsCompleted)
                        {
                            this._available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        }

                        available = this._available.Task;
                    }
                    else
                    {
                        this._hasOutstandingChunk = true;
                        available = Task.CompletedTask;
                    }
                }

                if (chunk is not null)
                {
                    chunk.CopyTo(buffer);
                    return chunk.Length;
                }

                await available.WaitAsync(cancellationToken);
            }
        }

        public Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Kill()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Advances a virtual clock as the provider awaits, releasing scripted output as its due time
    // passes so the schedule is expressed in the provider's own time rather than wall clock.
    private sealed class ScriptedTimeProvider(DateTimeOffset start, ScriptedPtySession session) : TimeProvider
    {
        private readonly List<(TimeSpan At, string Text)> _script = [];
        private readonly DateTimeOffset _start = start;
        private DateTimeOffset _now = start;

        public TimeSpan Elapsed => this._now - this._start;

        public void Schedule(TimeSpan at, string text) => this._script.Add((at, text));

        public override DateTimeOffset GetUtcNow() => this._now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            {
                this._now = this._now.Add(dueTime);

                List<string> due = [];
                this._script.RemoveAll(entry =>
                {
                    if (this._start + entry.At > this._now)
                    {
                        return false;
                    }

                    due.Add(entry.Text);
                    return true;
                });

                foreach (string text in due)
                {
                    session.Release(text);
                }
            }

            ImmediateTimer timer = new(callback, state);
            timer.Fire();
            return timer;
        }
    }

    private sealed class ImmediateTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => this._now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            {
                this._now = this._now.Add(dueTime);
            }

            ImmediateTimer timer = new(callback, state);
            timer.Fire();
            return timer;
        }
    }

    private sealed class ImmediateTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Fire() => ThreadPool.QueueUserWorkItem(_ => callback(state));

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
