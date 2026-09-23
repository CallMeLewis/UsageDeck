using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers.Claude;

namespace UsageDeck.Infrastructure.Tests;

public sealed class ClaudeUsageParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseReadsAPerModelLimitThatWasPaintedOverTheRowsBelowIt()
    {
        // Seen with Claude Code 2.1.278: cached limits are painted first, then the refreshed panel
        // inserts the per-model row by overwriting the rows beneath it. Only changed cells are
        // resent, so the new "used" arrives as "us" and "d" around an "e" that was already there,
        // and the stream never contains that row as readable text.
        const string output =
            "Current session\r\n"
            + "50% used\r\n"
            + "Resets 2:50pm (Europe/London)\r\n"
            + "Current week (all models)\r\n"
            + "12% used\r\n"
            + "Resets Sep 23, 4am (Europe/London)\r\n"
            + "What's contributing to your limits usage?\r\n"
            + "Include everything on this machine\r\n"
            + "\u001b[5;1H13"
            + "\u001b[7;1HCurrent week (Fable)\u001b[K"
            + "\u001b[8;1H23% us\u001b[1Cd\u001b[K"
            + "\u001b[9;1HResets Sep 23, 4am (Europe/London)";

        IReadOnlyList<UsageWindow> windows = ClaudeUsageParser.Parse(output, Now);

        Assert.Equal(["session", "weekly", "weekly-fable"], windows.Select(window => window.Id));
        Assert.Equal([50d, 13d, 23d], windows.Select(window => window.UsedPercent));
        Assert.All(windows, window => Assert.NotNull(window.ResetsAt));
    }

    [Fact]
    public void ParseMapsUsedAndRemainingQuotaWindows()
    {
        const string output = """
            Settings: Status   Config   Usage

            Current session
            12% used (Resets 3pm)
            Current week (all models)
            40% remaining (Resets Jul 20 at 8am)
            Current week (Sonnet only)
            5% used
            """;

        IReadOnlyList<UsageWindow> windows = ClaudeUsageParser.Parse(output, Now);

        Assert.Collection(
            windows,
            session =>
            {
                Assert.Equal("session", session.Id);
                Assert.Equal(12, session.UsedPercent);
            },
            weekly =>
            {
                Assert.Equal("weekly", weekly.Id);
                Assert.Equal(60, weekly.UsedPercent);
            },
            model =>
            {
                Assert.Equal("weekly-sonnet-only", model.Id);
                Assert.Equal(5, model.UsedPercent);
            });
    }

    // Verbatim from a real `claude /usage` capture. The panel is laid out with cursor
    // positioning, so stripping the escape sequences collapses every row onto one line.
    private const string CollapsedPanelCapture =
        "Current session\n\n  █▌ 3% usedResets 2:39am (Europe/London)"
        + "Current week (all models)0%usedResets Jul 29, 3:59am (Europe/London)"
        + "+50% weekly limits promo through Aug 19 · clau.de/cc-50-promo"
        + "Current week (Fable)0%usedWhat'scontributingtoyourlimitsusage?";

    [Fact]
    public void ParseReadsAResetThatWasPaddedWithCursorMovementInsteadOfSpaces()
    {
        // Verbatim from a real capture. Claude Code aligns some rows with cursor-positioning
        // escapes rather than literal spaces, so stripping them removes every space in the row.
        const string output =
            "Current session\n\n  █▌ 3% usedResets 2:40am (Europe/London)"
            + "Current week (Fable)▌1%usedResetsJul29,4am(Europe/London)What'scontributing";

        IReadOnlyList<UsageWindow> windows = ClaudeUsageParser.Parse(output, Now);

        UsageWindow fable = windows.Single(window => window.Id == "weekly-fable");
        DateTime expected = new(2026, 7, 29, 4, 0, 0);
        Assert.Equal(
            new DateTimeOffset(expected, TimeZoneInfo.Local.GetUtcOffset(expected)),
            fable.ResetsAt);
    }

    [Fact]
    public void ParseReadsAPanelFrameThatLostEveryLiteralSpace()
    {
        // Verbatim from a real capture. Claude Code sometimes lays the whole frame out with
        // cursor positioning rather than literal spaces, so stripping the escape sequences
        // leaves the panel - labels included - without a single space in it.
        const string output =
            "Currentsession\n\n  ██████████    20% usedResets2:40am(Europe/London)"
            + "Currentweek(allmodels)▌1%usedResetsJul29,4am(Europe/London)"
            + "+50%weeklylimitspromothroughAug19·clau.de/cc-50-promo"
            + "Currentweek(Fable)▌2%usedWhat'scontributingtoyourlimitsusage?";

        IReadOnlyList<UsageWindow> windows = ClaudeUsageParser.Parse(output, Now);

        Assert.Equal(["session", "weekly", "weekly-fable"], windows.Select(window => window.Id));
        Assert.Equal(20, windows[0].UsedPercent);
        Assert.Equal("Weekly limit", windows[1].DisplayName);
        Assert.Equal("Fable weekly", windows[2].DisplayName);

        DateTime expected = new(2026, 7, 29, 4, 0, 0);
        Assert.Equal(
            new DateTimeOffset(expected, TimeZoneInfo.Local.GetUtcOffset(expected)),
            windows[1].ResetsAt);
    }

    [Fact]
    public void ParseReadsWeeklyResetWhenTrailingBannerTextFollowsItOnTheSameLine()
    {
        IReadOnlyList<UsageWindow> windows = ClaudeUsageParser.Parse(CollapsedPanelCapture, Now);

        UsageWindow weekly = windows.Single(window => window.Id == "weekly");
        DateTime expected = new(2026, 7, 29, 3, 59, 0);
        Assert.Equal(
            new DateTimeOffset(expected, TimeZoneInfo.Local.GetUtcOffset(expected)),
            weekly.ResetsAt);
    }

    [Fact]
    public void ParseReadsSessionResetWhenATimezoneSuffixFollowsIt()
    {
        IReadOnlyList<UsageWindow> windows = ClaudeUsageParser.Parse(CollapsedPanelCapture, Now);

        UsageWindow session = windows.Single(window => window.Id == "session");
        Assert.Equal(3, session.UsedPercent);
        Assert.NotNull(session.ResetsAt);
        Assert.Equal(2, session.ResetsAt!.Value.Hour);
        Assert.Equal(39, session.ResetsAt.Value.Minute);
    }

    [Fact]
    public void ParseCollapsesRepaintedPanelFramesToOneWindowPerLimit()
    {
        // Claude Code repaints the /usage panel while it is open. Because cursor-movement
        // sequences are stripped rather than replayed, an overwritten frame is captured as
        // additional text, so the same limit appears once per frame.
        const string output = """
            Current session
            100% remaining (Resets 2:40am)
            Current week (all models)
            100% remaining

            Current session
            100% remaining (Resets 2:40am)
            Current week (all models)
            100% remaining
            Current week (Fable)
            100% remaining
            """;

        IReadOnlyList<UsageWindow> windows = ClaudeUsageParser.Parse(output, Now);

        Assert.Equal(["session", "weekly", "weekly-fable"], windows.Select(window => window.Id));
    }

    [Fact]
    public void ParsePrefersTheMostRecentFrameForARepeatedLimit()
    {
        const string output = """
            Current session
            10% used (Resets 2:40am)

            Current session
            35% used (Resets 2:40am)
            """;

        UsageWindow window = Assert.Single(ClaudeUsageParser.Parse(output, Now));

        Assert.Equal(35, window.UsedPercent);
    }

    [Fact]
    public void ParseStripsAnsiSequences()
    {
        const string output = "\u001b[35mCurrent session\u001b[0m\r\n20% left\r\n";

        UsageWindow window = Assert.Single(ClaudeUsageParser.Parse(output, Now));

        Assert.Equal(80, window.UsedPercent);
    }

    [Fact]
    public void ParsePrintedReadsTheLimitsThatPrintModeListsOnePerLine()
    {
        // Captured from `claude -p /usage` on Claude Code 2.1.280.
        const string output = """
            You are currently using your subscription to power your Claude Code usage

            Current session: 7% used · resets Jul 16, 11:29pm (Europe/London)
            Current week (all models): 2% used · resets Jul 22, 3:59am (Europe/London)
            Current week (Fable): 0% used · resets Jul 22, 4am (Europe/London)

            What's contributing to your limits usage?
            Last 7d · 1413 requests · 22 sessions
              70% of your usage was at >150k context
            """;

        IReadOnlyList<UsageWindow> windows = ClaudeUsageParser.ParsePrinted(output, Now);

        Assert.Equal(["session", "weekly", "weekly-fable"], windows.Select(window => window.Id));
        Assert.Equal([7d, 2d, 0d], windows.Select(window => window.UsedPercent));
        Assert.Equal(
            [new DateTime(2026, 7, 16, 23, 29, 0), new DateTime(2026, 7, 22, 3, 59, 0), new DateTime(2026, 7, 22, 4, 0, 0)],
            windows.Select(window => window.ResetsAt!.Value.DateTime));
    }

    [Fact]
    public void ParsePrintedClassifiesACostOnlySummaryAsQuotaUnavailable()
    {
        const string output = """
            Total cost:            $0.0000
            Total duration (API):  0s
            """;

        ProviderException exception = Assert.Throws<ProviderException>(
            () => ClaudeUsageParser.ParsePrinted(output, Now));

        Assert.Equal(ProviderErrorCategory.Unavailable, exception.Category);
    }

    [Theory]
    [InlineData("You are currently using your subscription to power your Claude Code usage")]
    [InlineData("Settings Status Config Usage Stats\nSession\nTotal cost: $0.0000")]
    public void ParseClassifiesQuotaUnavailableScreens(string output)
    {
        ProviderException exception = Assert.Throws<ProviderException>(() => ClaudeUsageParser.Parse(output, Now));

        Assert.Equal(ProviderErrorCategory.Unavailable, exception.Category);
    }
}
