using System.Text;
using System.Text.Json;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Compatibility;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Providers.Amp;
using UsageDeck.Infrastructure.Providers.Antigravity;
using UsageDeck.Infrastructure.Providers.Claude;
using UsageDeck.Infrastructure.Providers.Codex;
using UsageDeck.Infrastructure.Providers.Copilot;
using UsageDeck.Infrastructure.Providers.Kiro;

if (args.Length > 0 && string.Equals(args[0], "claude-capture", StringComparison.OrdinalIgnoreCase))
{
    await CaptureClaudeUsageAsync();
    return;
}

ExecutableLocator executableLocator = new();
IUsageProvider provider = args switch
{
    [] => CreateCodexProvider(ProviderHost.Native),
    [var name] when name.Equals("codex", StringComparison.OrdinalIgnoreCase) => CreateCodexProvider(ProviderHost.Native),
    [var name] when name.Equals("claude", StringComparison.OrdinalIgnoreCase) =>
        new ClaudeUsageProvider(new PtySessionFactory(), executableLocator),
    [var name] when name.Equals("antigravity", StringComparison.OrdinalIgnoreCase) =>
        new AntigravityUsageProvider(new PtySessionFactory(), executableLocator),
    [var name] when name.Equals("copilot", StringComparison.OrdinalIgnoreCase) =>
        new CopilotUsageProvider(new ProcessSessionFactory(), executableLocator),
    [var name] when name.Equals("kiro", StringComparison.OrdinalIgnoreCase) =>
        new KiroUsageProvider(new ProcessSessionFactory(), new PtySessionFactory(), executableLocator),
    [var name] when name.Equals("amp", StringComparison.OrdinalIgnoreCase) =>
        new AmpUsageProvider(new ProcessSessionFactory(), executableLocator),
    [var name, var option, var distribution]
        when name.Equals("codex", StringComparison.OrdinalIgnoreCase)
        && option.Equals("--wsl", StringComparison.OrdinalIgnoreCase) => CreateCodexProvider(ProviderHost.Wsl(distribution)),
    _ => throw new ArgumentException(
        "Usage: UsageDeck.Probe [codex [--wsl <distribution>] | claude | claude-capture | antigravity | copilot | kiro | amp]")
};

try
{
    ProviderSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);
    object redacted = new
    {
        provider = snapshot.ProviderId.Value,
        source = snapshot.SourceDescription,
        state = snapshot.State.ToString(),
        capturedAt = snapshot.CapturedAt,
        plan = snapshot.Identity?.Plan,
        hasAccountIdentity = snapshot.Identity is not null,
        credits = snapshot.Credits is null
            ? null
            : new { snapshot.Credits.HasCredits, snapshot.Credits.IsUnlimited, snapshot.Credits.Balance },
        resetCredits = snapshot.ResetCredits is null
            ? null
            : new
            {
                snapshot.ResetCredits.AvailableCount,
                credits = snapshot.ResetCredits.Credits.Select(credit => new { credit.ExpiresAt }),
            },
        windows = snapshot.UsageWindows.Select(window => new
        {
            window.Id,
            window.DisplayName,
            window.UsedPercent,
            window.ResetsAt,
            window.Duration,
        }),
    };

    Console.WriteLine(JsonSerializer.Serialize(redacted, new JsonSerializerOptions { WriteIndented = true }));
}
catch (ProviderException exception)
{
    Console.Error.WriteLine(exception.SafeMessage);
    Environment.ExitCode = 1;
}

CodexUsageProvider CreateCodexProvider(ProviderHost host) => new(
    new ProcessSessionFactory(),
    new CodexProcessSpecFactory(executableLocator),
    host);

static async Task CaptureClaudeUsageAsync()
{
    string? claudePath = new ExecutableLocator().FindExecutable("claude");
    if (claudePath is null)
    {
        throw new InvalidOperationException("Claude CLI was not found.");
    }

    string workingDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationIdentity.LocalDataDirectoryName,
        "ClaudeProbe");
    Directory.CreateDirectory(workingDirectory);

    PtyStartSpec spec = new(
        claudePath,
        ["--allowedTools", "", "--permission-mode", "plan"],
        workingDirectory,
        new Dictionary<string, string>
        {
            ["CLAUDE_CODE_DISABLE_TERMINAL_TITLE"] = "1",
            ["DISABLE_AUTOUPDATER"] = "1",
        });

    await using IPtySession session = await new PtySessionFactory().StartAsync(spec, CancellationToken.None);
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

    byte[] buffer = new byte[4096];
    StringBuilder captured = new(capacity: 32_768);
    Task captureTask = CaptureAsync();

    // Claude Code paints its prompt asynchronously. Sending a slash command before the
    // initial render completes can leave it in the terminal input buffer without executing.
    await Task.Delay(TimeSpan.FromSeconds(4), timeout.Token);
    await session.WriteAsync(Encoding.UTF8.GetBytes("/usage"), timeout.Token);
    await Task.Delay(TimeSpan.FromMilliseconds(150), timeout.Token);
    await session.WriteAsync("\r"u8.ToArray(), timeout.Token);

    try
    {
        await captureTask;
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
    }
    finally
    {
        session.Kill();
    }

    ClaudeUsageDiagnostic diagnostic = ClaudeUsageDiagnostics.Create(
        captured.ToString(),
        DateTimeOffset.UtcNow);
    Console.WriteLine(JsonSerializer.Serialize(
        diagnostic,
        new JsonSerializerOptions { WriteIndented = true }));

    async Task CaptureAsync()
    {
        while (captured.Length < 262_144)
        {
            int read = await session.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                break;
            }

            captured.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }
}
