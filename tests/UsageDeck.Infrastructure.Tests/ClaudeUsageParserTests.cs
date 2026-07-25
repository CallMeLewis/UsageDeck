using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers.Claude;

namespace UsageDeck.Infrastructure.Tests;

public sealed class ClaudeUsageParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);

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

    [Theory]
    [InlineData("You are currently using your subscription to power your Claude Code usage")]
    [InlineData("Settings Status Config Usage Stats\nSession\nTotal cost: $0.0000")]
    public void ParseClassifiesQuotaUnavailableScreens(string output)
    {
        ProviderException exception = Assert.Throws<ProviderException>(() => ClaudeUsageParser.Parse(output, Now));

        Assert.Equal(ProviderErrorCategory.Unavailable, exception.Category);
    }
}
