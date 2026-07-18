using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Providers.Antigravity;

namespace UsageDeck.Infrastructure.Tests;

public sealed class AntigravityUsageProviderTests
{
    [Fact]
    public async Task FetchUsesUsageCommandAndCleansUpPty()
    {
        FakePtySession session = new("Model Quotas\nGemini 3.1 Pro 40% used");
        FakePtySessionFactory sessions = new(session);
        AntigravityUsageProvider provider = new(
            sessions,
            new StubExecutableLocator("C:\\tools\\agy.exe"),
            new ImmediateTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero)));

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderId.Antigravity, snapshot.ProviderId);
        Assert.Equal("Antigravity CLI", snapshot.SourceDescription);
        Assert.Equal(40, Assert.Single(snapshot.UsageWindows).UsedPercent);
        Assert.Contains("/usage", session.WrittenText, StringComparison.Ordinal);
        Assert.Equal("C:\\tools\\agy.exe", sessions.StartSpec?.ExecutablePath);
        Assert.Empty(sessions.StartSpec?.Arguments ?? ["unexpected"]);
        Assert.True(session.WasKilled);
        Assert.True(session.WasDisposed);
    }

    [Fact]
    public async Task FetchPreservesCallerCancellation()
    {
        FakePtySession session = new("Model Quotas\nGemini 3.1 Pro 40% used");
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

    private sealed class FakePtySession(string output) : IPtySession
    {
        private readonly byte[] _output = Encoding.UTF8.GetBytes(output);
        private readonly StringBuilder _written = new();
        private bool _hasReadOutput;

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
