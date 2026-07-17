using System.Text;
using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Processes;
using CodexBarWin.Infrastructure.Providers.Claude;

namespace CodexBarWin.Infrastructure.Tests;

public sealed class ClaudeUsageProviderTests
{
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
