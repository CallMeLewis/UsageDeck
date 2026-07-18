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
