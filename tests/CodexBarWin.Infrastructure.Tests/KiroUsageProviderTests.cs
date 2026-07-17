using System.Text;
using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Processes;
using CodexBarWin.Infrastructure.Providers.Kiro;

namespace CodexBarWin.Infrastructure.Tests;

public sealed class KiroUsageProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FetchRunsHeadlessUsageCommandWithoutHandlingCredentials()
    {
        FakeProcessSessionFactory processes = new("""
            Estimated Usage | resets on 2026-08-01 | KIRO FREE
            Credits (12.50 of 50 covered in plan)
            ████████████████████ 25%
            """);
        RejectingPtySessionFactory pty = new();
        RecordingCliVersionReader versionReader = new("1.26.0");
        KiroUsageProvider provider = new(
            processes,
            pty,
            new StubExecutableLocator("C:\\tools\\kiro-cli.exe"),
            new FixedTimeProvider(Now),
            versionReader);

        string? cliVersion = await provider.ReadCliVersionAsync(CancellationToken.None);
        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderId.Kiro, snapshot.ProviderId);
        Assert.Equal(25, Assert.Single(snapshot.UsageWindows).UsedPercent);
        Assert.Equal("C:\\tools\\kiro-cli.exe", processes.StartSpec?.ExecutablePath);
        Assert.Equal(["chat", "--no-interactive", "/usage"], processes.StartSpec?.Arguments);
        Assert.Equal("1", processes.StartSpec?.Environment?["NO_COLOR"]);
        Assert.False(pty.WasStarted);
        Assert.Equal("1.26.0", cliVersion);
        Assert.Equal("C:\\tools\\kiro-cli.exe", versionReader.StartSpec?.ExecutablePath);
        Assert.Equal(["--version"], versionReader.StartSpec?.Arguments);
    }

    [Fact]
    public async Task FetchFallsBackToAnIsolatedTerminalForIncompletePipeOutput()
    {
        FakeProcessSessionFactory processes = new("Plan: loading...");
        FakePtySessionFactory pty = new("""
            Estimated Usage | resets on 2026-08-01 | KIRO FREE
            Credits (10 of 50 covered in plan)
            ████████████████████ 20%
            """);
        KiroUsageProvider provider = new(
            processes,
            pty,
            new StubExecutableLocator("C:\\tools\\kiro-cli.exe"),
            new FixedTimeProvider(Now));

        ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(20, Assert.Single(snapshot.UsageWindows).UsedPercent);
        Assert.NotNull(pty.StartSpec);
        Assert.Equal(["chat", "--no-interactive", "/usage"], pty.StartSpec?.Arguments);
    }

    [Fact]
    public async Task FetchReportsWhenKiroIsNotInstalled()
    {
        KiroUsageProvider provider = new(
            new FakeProcessSessionFactory("unused"),
            new RejectingPtySessionFactory(),
            new StubExecutableLocator(null));

        ProviderException exception = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.FetchAsync(CancellationToken.None));

        Assert.Equal(ProviderErrorCategory.NotInstalled, exception.Category);
    }

    [Fact]
    public async Task FetchPreservesCallerCancellation()
    {
        KiroUsageProvider provider = new(
            new FakeProcessSessionFactory("unused"),
            new RejectingPtySessionFactory(),
            new StubExecutableLocator("C:\\tools\\kiro-cli.exe"));
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.FetchAsync(cancellation.Token));
    }

    private sealed class StubExecutableLocator(string? path) : IExecutableLocator
    {
        public string? FindExecutable(string executableName) => path;
    }

    private sealed class RecordingCliVersionReader(string version) : ICliVersionReader
    {
        public ProcessStartSpec? StartSpec { get; private set; }

        public Task<string?> ReadAsync(ProcessStartSpec spec, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.StartSpec = spec;
            return Task.FromResult<string?>(version);
        }
    }

    private sealed class FakeProcessSessionFactory(string response) : IProcessSessionFactory
    {
        public ProcessStartSpec? StartSpec { get; private set; }

        public IProcessSession Start(ProcessStartSpec spec)
        {
            this.StartSpec = spec;
            return new FakeProcessSession(response);
        }
    }

    private sealed class FakeProcessSession(string response) : IProcessSession
    {
        private string? _response = response;

        public Task WriteLineAsync(string line, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? result = this._response;
            this._response = null;
            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RejectingPtySessionFactory : IPtySessionFactory
    {
        public bool WasStarted { get; private set; }

        public Task<IPtySession> StartAsync(PtyStartSpec spec, CancellationToken cancellationToken)
        {
            this.WasStarted = true;
            throw new InvalidOperationException("The PTY fallback was not expected.");
        }
    }

    private sealed class FakePtySessionFactory(string response) : IPtySessionFactory
    {
        public PtyStartSpec? StartSpec { get; private set; }

        public Task<IPtySession> StartAsync(PtyStartSpec spec, CancellationToken cancellationToken)
        {
            this.StartSpec = spec;
            return Task.FromResult<IPtySession>(new FakePtySession(response));
        }
    }

    private sealed class FakePtySession(string response) : IPtySession
    {
        private byte[]? _response = Encoding.UTF8.GetBytes(response);

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this._response is null)
            {
                return Task.FromResult(0);
            }

            int length = Math.Min(buffer.Length, this._response.Length);
            this._response.AsMemory(0, length).CopyTo(buffer);
            this._response = null;
            return Task.FromResult(length);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Kill()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
