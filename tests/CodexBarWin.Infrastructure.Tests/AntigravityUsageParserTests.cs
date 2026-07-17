using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Providers.Antigravity;

namespace CodexBarWin.Infrastructure.Tests;

public sealed class AntigravityUsageParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseMapsGroupedQuotaWindowsAndRelativeResets()
    {
        const string output = """
            Model Quotas
            Gemini Models
            Weekly limit 35% used
            Resets in 2d 4h
            5-hour limit 80% remaining
            Resets in 3h
            Claude and GPT models
            Weekly limit
            60% remaining
            Resets in 5d
            """;

        IReadOnlyList<UsageWindow> windows = AntigravityUsageParser.Parse(output, Now);

        Assert.Collection(
            windows,
            weekly =>
            {
                Assert.Equal("gemini-weekly", weekly.Id);
                Assert.Equal(35, weekly.UsedPercent);
                Assert.Equal(Now.AddDays(2).AddHours(4), weekly.ResetsAt);
            },
            session =>
            {
                Assert.Equal("gemini-session", session.Id);
                Assert.Equal(20, session.UsedPercent);
                Assert.Equal(Now.AddHours(3), session.ResetsAt);
            },
            weekly =>
            {
                Assert.Equal("claude-gpt-weekly", weekly.Id);
                Assert.Equal(40, weekly.UsedPercent);
                Assert.Equal(Now.AddDays(5), weekly.ResetsAt);
            });
    }

    [Fact]
    public void ParseKeepsModelSpecificRowsAndStripsTerminalSequences()
    {
        const string output = "\u001b[2J\u001b[1;1HGemini 3.1 Pro\r\n\u001b[32m72% remaining\u001b[0m";

        UsageWindow window = Assert.Single(AntigravityUsageParser.Parse(output, Now));

        Assert.Equal("gemini-3-1-pro", window.Id);
        Assert.Equal(28, window.UsedPercent);
    }

    [Fact]
    public void ParseClassifiesSignedOutScreen()
    {
        ProviderException exception = Assert.Throws<ProviderException>(() =>
            AntigravityUsageParser.Parse("Sign in with Google to continue", Now));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
    }
}
