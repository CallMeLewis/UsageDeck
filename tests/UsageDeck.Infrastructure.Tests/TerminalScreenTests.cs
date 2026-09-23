using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Tests;

public sealed class TerminalScreenTests
{
    [Fact]
    public void RenderAppliesInPlaceUpdatesThatOnlySendTheChangedCells()
    {
        // A pseudo-terminal resends only the cells that differ, so the stream never contains the
        // updated word: "12% uses" becomes "23% used" through "23" and a lone "d".
        string output = "Weekly\r\n12% uses\r\n"
            + "\u001b[2;1H23"
            + "\u001b[2;8Hd";

        Assert.Equal("Weekly\n23% used", TerminalScreen.Render(output, 40, 10));
    }

    [Fact]
    public void RenderFollowsRelativeCursorMovesAndLineErasure()
    {
        string output = "first line\r\nsecond line\r\nthird"
            + "\u001b[2A\u001b[1Greplaced\u001b[K"
            + "\u001b[1B\u001b[8Gword";

        Assert.Equal("replaced\nsecond word\nthird", TerminalScreen.Render(output, 40, 10));
    }

    [Fact]
    public void RenderIgnoresColourModeAndTitleSequences()
    {
        string output = "\u001b]0;window title\u0007\u001b[?25l\u001b[1;32mgreen\u001b[0m text\u001b(B";

        Assert.Equal("green text", TerminalScreen.Render(output, 40, 10));
    }

    [Fact]
    public void RenderKeepsRowsThatScrollOffTheTop()
    {
        string output = "one\r\ntwo\r\nthree\r\nfour";

        Assert.Equal("one\ntwo\nthree\nfour", TerminalScreen.Render(output, 40, 2));
    }

    [Fact]
    public void RenderTreatsABareLineFeedAsANewLine()
    {
        Assert.Equal("one\ntwo", TerminalScreen.Render("one\ntwo", 40, 10));
    }
}
