using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers.Amp;

namespace UsageDeck.Infrastructure.Tests;

public sealed class AmpUsageParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseMapsCurrentFreeUsageCreditsAndIdentity()
    {
        string output = """
            [2mSigned in as amp@example.com (Example Team)[0m
            Amp Free: $4.71/$10 remaining (replenishes +$0.42/hour) - https://ampcode.com/settings#amp-free
            Individual credits: $25.64 remaining
            Workspace Alpha Team: $1,234.56 remaining
            """;

        ProviderSnapshot snapshot = AmpUsageParser.Parse(output, Now);

        Assert.Equal(ProviderId.Amp, snapshot.ProviderId);
        Assert.Equal("Amp CLI", snapshot.SourceDescription);
        UsageWindow window = Assert.Single(snapshot.UsageWindows);
        Assert.Equal("Amp Free", window.DisplayName);
        Assert.Equal(52.9, window.UsedPercent, precision: 3);
        Assert.Equal(TimeSpan.FromHours(24), window.Duration);
        Assert.Equal(Now.AddHours(5.29 / 0.42), window.ResetsAt);
        Assert.Equal("amp@example.com", snapshot.Identity?.Email);
        Assert.Equal("Example Team", snapshot.Identity?.Organisation);
        Assert.Equal("Amp Free", snapshot.Identity?.Plan);
        Assert.Equal(
            "Individual: $25.64 · Workspace Alpha Team: $1,234.56",
            snapshot.Credits?.Balance);
    }

    [Fact]
    public void ParseMapsPercentageBasedDailyUsage()
    {
        string output = """
            Signed in as user@example.com
            Amp Free: 61% remaining today (resets daily)
            Individual credits: $9.86 remaining
            """;

        ProviderSnapshot snapshot = AmpUsageParser.Parse(output, Now);

        UsageWindow window = Assert.Single(snapshot.UsageWindows);
        Assert.Equal(39, window.UsedPercent);
        Assert.Equal(TimeSpan.FromHours(24), window.Duration);
        Assert.Null(window.ResetsAt);
        Assert.Equal("Individual: $9.86", snapshot.Credits?.Balance);
    }

    [Fact]
    public void ParsePrefersReplenishingBalanceWhenBothFreeFormatsExist()
    {
        string output = """
            Signed in as user@example.com
            Amp Free: $6/$10 remaining (replenishes +$0.5/hour)
            Amp Free: 61% remaining today (resets daily)
            """;

        UsageWindow window = Assert.Single(AmpUsageParser.Parse(output, Now).UsageWindows);

        Assert.Equal(40, window.UsedPercent);
        Assert.Equal(Now.AddHours(8), window.ResetsAt);
    }

    [Fact]
    public void ParseAllowsCreditOnlyAccounts()
    {
        string output = """
            Signed in as paid@example.com (Paid Team)
            Workspace Beta: $7 remaining
            """;

        ProviderSnapshot snapshot = AmpUsageParser.Parse(output, Now);

        Assert.Empty(snapshot.UsageWindows);
        Assert.Equal("Workspace Beta: $7", snapshot.Credits?.Balance);
        Assert.Null(snapshot.Identity?.Plan);
    }

    [Fact]
    public void ParseReportsAuthenticationRequired()
    {
        ProviderException exception = Assert.Throws<ProviderException>(() =>
            AmpUsageParser.Parse("Please sign in to Amp.", Now));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
        Assert.Contains("amp login", exception.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsIncompleteOutput()
    {
        ProviderException exception = Assert.Throws<ProviderException>(() =>
            AmpUsageParser.Parse("Amp usage is loading...", Now));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
    }
}
