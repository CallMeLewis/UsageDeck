using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Providers.Antigravity;

namespace UsageDeck.Infrastructure.Tests;

public sealed class AntigravityUsageProviderTests
{
    private const string StartupScreen =
        "Welcome to the Antigravity CLI. You are currently not signed in.\n Signing in...\n";

    private const string ReadyScreen =
        "Antigravity CLI 1.5.13\n>\n? for shortcuts\nauto · Gemini 4 Neon · high\n";

    [Fact]
    public async Task FetchUsesUsageCommandAndCleansUpPty()
    {
        FakePtySession session = new(ReadyScreen, "Model Quotas\nGemini 3.1 Pro 40% used");
        FakePtySessionFactory sessions = new(session);
        AntigravityUsageProvider provider = new(
            sessions,
            new StubExecutableLocator("C:\\tools\\agy.exe"),
            new ImmediateTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero)));

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderId.Antigravity, snapshot.ProviderId);
        Assert.Equal("Antigravity CLI backend quota", snapshot.SourceDescription);
        Assert.Equal(UsageDataCoverage.SignedInAccount, snapshot.Coverage);
        Assert.Equal(40, Assert.Single(snapshot.UsageWindows).UsedPercent);
        Assert.Contains("/usage", session.WrittenText, StringComparison.Ordinal);
        Assert.Equal("C:\\tools\\agy.exe", sessions.StartSpec?.ExecutablePath);
        Assert.Empty(sessions.StartSpec?.Arguments ?? ["unexpected"]);
        Assert.True(session.WasKilled);
        Assert.True(session.WasDisposed);
    }

    [Fact]
    public async Task FetchTurnsOffAgyBackgroundUpdater()
    {
        FakePtySession session = new(ReadyScreen, "Model Quotas\nGemini 3.1 Pro 40% used");
        FakePtySessionFactory sessions = new(session);
        AntigravityUsageProvider provider = new(
            sessions,
            new StubExecutableLocator("C:\\tools\\agy.exe"),
            new ImmediateTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero)));

        await provider.FetchAsync(CancellationToken.None);

        // agy ignores "1" for this variable. Only the literal "true" stops its background
        // updater, which otherwise opens a console window on Windows.
        Assert.Equal("true", sessions.StartSpec?.Environment?["AGY_CLI_DISABLE_AUTO_UPDATE"]);
    }

    [Fact]
    public async Task FetchWaitsForPromptAfterSavedSignInLoads()
    {
        DateTimeOffset start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        ImmediateTimeProvider time = new(start);

        // agy shows its prompt about five seconds in, once the saved sign-in has loaded.
        FakePtySession session = new(
            [StartupScreen, ReadyScreen],
            "Models & Quota\nGEMINI MODELS\n  Weekly Limit Remaining\n    [████░] 99.62%\n    Refreshes in 167h 54m\n",
            chunk => chunk == 0 ? Task.CompletedTask : time.WhenReached(start.AddSeconds(5)));
        AntigravityUsageProvider provider = new(
            new FakePtySessionFactory(session),
            new StubExecutableLocator("C:\\tools\\agy.exe"),
            time);

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("Gemini weekly", Assert.Single(snapshot.UsageWindows).DisplayName);
        Assert.Equal(2, session.ChunksReadBeforeCommand);
    }

    [Fact]
    public async Task FetchReportsSignInWhenAgyAsksForBrowserSignIn()
    {
        FakePtySession session = new(
            StartupScreen + "Select login method:\n> Sign in with Google\n",
            "unexpected");
        AntigravityUsageProvider provider = new(
            new FakePtySessionFactory(session),
            new StubExecutableLocator("C:\\tools\\agy.exe"),
            new ImmediateTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero)));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
        Assert.Empty(session.WrittenText);
        Assert.True(session.WasKilled);
    }

    [Fact]
    public async Task FetchPreservesCallerCancellation()
    {
        FakePtySession session = new(ReadyScreen, "Model Quotas\nGemini 3.1 Pro 40% used");
        AntigravityUsageProvider provider = new(
            new FakePtySessionFactory(session),
            new StubExecutableLocator("C:\\tools\\agy.exe"));
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.FetchAsync(cancellation.Token));
    }

    private sealed class StubExecutableLocator(string path) : IExecutableLocator
    {
        public string? FindExecutable(string executableName) => path;
    }

    private sealed class FakePtySessionFactory(FakePtySession session) : IPtySessionFactory
    {
        public PtyStartSpec? StartSpec { get; private set; }

        public Task<IPtySession> StartAsync(PtyStartSpec spec, CancellationToken cancellationToken)
        {
            this.StartSpec = spec;
            return Task.FromResult<IPtySession>(session);
        }
    }

    /// <summary>
    /// Plays <paramref name="startup"/> one chunk per read, each once <paramref name="release"/>
    /// completes for it, then holds <paramref name="afterCommand"/> back until `/usage` is typed,
    /// as agy only draws the quota panel in response to the command.
    /// </summary>
    private sealed class FakePtySession(
        string[] startup,
        string afterCommand,
        Func<int, Task>? release = null) : IPtySession
    {
        private readonly Queue<string> _startup = new(startup);
        private readonly StringBuilder _written = new();
        private readonly TaskCompletionSource _commandSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startupChunksRead;
        private bool _hasReadAfterCommand;

        public FakePtySession(string startup, string afterCommand)
            : this([startup], afterCommand)
        {
        }

        public bool WasKilled { get; private set; }

        public bool WasDisposed { get; private set; }

        public int ChunksReadBeforeCommand { get; private set; }

        public string WrittenText => this._written.ToString();

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (this._startup.Count > 0 && release is not null)
            {
                // Awaited directly so the chunk arrives inline with the release, not on a later
                // thread-pool turn that the provider's polling could overtake.
                await release(this._startupChunksRead);
            }

            if (this._startup.TryDequeue(out string? chunk))
            {
                this._startupChunksRead++;
                if (!this._commandSent.Task.IsCompleted)
                {
                    this.ChunksReadBeforeCommand++;
                }

                return Copy(chunk, buffer);
            }

            await this._commandSent.Task.WaitAsync(cancellationToken);
            if (!this._hasReadAfterCommand)
            {
                this._hasReadAfterCommand = true;
                return Copy(afterCommand, buffer);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            this._written.Append(Encoding.UTF8.GetString(buffer.Span));
            if (this.WrittenText.Contains("/usage", StringComparison.Ordinal))
            {
                this._commandSent.TrySetResult();
            }

            return Task.CompletedTask;
        }

        private static int Copy(string text, Memory<byte> buffer)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }

        public void Kill() => this.WasKilled = true;

        public ValueTask DisposeAsync()
        {
            this.WasDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ImmediateTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly List<(DateTimeOffset At, TaskCompletionSource Reached)> _waiters = [];
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => this._now;

        /// <summary>
        /// Completes once the clock has moved on to <paramref name="at"/>. Continuations run
        /// inside the timer request that moves the clock, before that timer fires.
        /// </summary>
        public Task WhenReached(DateTimeOffset at)
        {
            lock (this._waiters)
            {
                if (this._now >= at)
                {
                    return Task.CompletedTask;
                }

                TaskCompletionSource reached = new();
                this._waiters.Add((at, reached));
                return reached.Task;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            {
                List<TaskCompletionSource> reached = [];
                lock (this._waiters)
                {
                    this._now = this._now.Add(dueTime);
                    reached.AddRange(this._waiters.Where(waiter => this._now >= waiter.At).Select(waiter => waiter.Reached));
                    this._waiters.RemoveAll(waiter => this._now >= waiter.At);
                }

                foreach (TaskCompletionSource waiter in reached)
                {
                    waiter.TrySetResult();
                }
            }

            ImmediateTimer timer = new(callback, state);
            timer.Fire();
            return timer;
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
}
