using System.Text;
using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Tests;

public sealed class ProcessSessionFactoryTests
{
    [Theory]
    [InlineData("codex-cli 0.144.5", "0.144.5")]
    [InlineData("2.1.212 (Claude Code)", "2.1.212")]
    [InlineData("gh version 2.87.3 (2026-02-23)", "2.87.3")]
    [InlineData("kiro-cli v1.26.0-alpha.2+build.7", "1.26.0-alpha.2+build.7")]
    public async Task CliVersionReaderExtractsOnlyTheSemanticVersion(string output, string expected)
    {
        CliVersionReader reader = new(new FixedProcessSessionFactory([output]));

        string? version = await reader.ReadAsync(
            new ProcessStartSpec("provider.exe", ["--version"]),
            CancellationToken.None);

        Assert.Equal(expected, version);
    }

    [Fact]
    public async Task CliVersionReaderIgnoresOutputWithoutAVersion()
    {
        CliVersionReader reader = new(new FixedProcessSessionFactory(["signed in as developer@example.com"]));

        string? version = await reader.ReadAsync(
            new ProcessStartSpec("provider.exe", ["--version"]),
            CancellationToken.None);

        Assert.Null(version);
    }

    [Fact]
    public async Task ProcessSessionWritesReadsAndDisposesRunningProcess()
    {
        string commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        IProcessSession session = new ProcessSessionFactory().Start(new ProcessStartSpec(
            commandInterpreter,
            ["/d", "/q"],
            Environment.CurrentDirectory));

        await using (session)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            await session.WriteLineAsync("echo process-session-ready", timeout.Token);
            string? line;
            do
            {
                line = await session.ReadLineAsync(timeout.Token);
            }
            while (line is not null && !line.Contains("process-session-ready", StringComparison.OrdinalIgnoreCase));

            Assert.Contains("process-session-ready", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task PtySessionStartsCapturesOutputAndCleansUp()
    {
        string commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        await using IPtySession session = await new PtySessionFactory().StartAsync(
            new PtyStartSpec(
                commandInterpreter,
                ["/d", "/q"],
                Environment.CurrentDirectory),
            CancellationToken.None);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
        await session.WriteAsync(Encoding.UTF8.GetBytes("echo pty-session-ready\r\n"), timeout.Token);

        byte[] buffer = new byte[4096];
        StringBuilder output = new();
        while (!output.ToString().Contains("pty-session-ready", StringComparison.OrdinalIgnoreCase))
        {
            int read = await session.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                break;
            }

            output.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        Assert.Contains("pty-session-ready", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedProcessSessionFactory(IEnumerable<string> output) : IProcessSessionFactory
    {
        public IProcessSession Start(ProcessStartSpec spec) => new FixedProcessSession(output);
    }

    private sealed class FixedProcessSession(IEnumerable<string> output) : IProcessSession
    {
        private readonly Queue<string> _output = new(output);

        public Task WriteLineAsync(string line, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(this._output.TryDequeue(out string? line) ? line : null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
