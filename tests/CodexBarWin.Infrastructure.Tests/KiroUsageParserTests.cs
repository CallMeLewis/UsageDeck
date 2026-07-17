using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Providers.Kiro;

namespace CodexBarWin.Infrastructure.Tests;

public sealed class KiroUsageParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseMapsCurrentKiroUsageAndBonusCredits()
    {
        const string output = """
            [1mEstimated Usage[0m | resets on 2026-08-01 | [mKIRO FREE[0m

            🎁 Bonus credits: 45.53/2000 credits used, expires in 19 days

            [1mCredits[0m (0.17 of 50 covered in plan)
            ████████████████████████████████████████████████████████████████████████████████ 0%

            Overages: Disabled
            """;

        ProviderSnapshot snapshot = KiroUsageParser.Parse(output, Now);

        Assert.Equal(ProviderId.Kiro, snapshot.ProviderId);
        Assert.Equal("Kiro CLI", snapshot.SourceDescription);
        Assert.Equal("KIRO FREE", snapshot.Identity?.Plan);
        Assert.Collection(
            snapshot.UsageWindows,
            plan =>
            {
                Assert.Equal("plan-credits", plan.Id);
                Assert.Equal(0.34, plan.UsedPercent, precision: 8);
                Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), plan.ResetsAt);
            },
            bonus =>
            {
                Assert.Equal("bonus-credits", bonus.Id);
                Assert.Equal(45.53 / 2000 * 100, bonus.UsedPercent, precision: 8);
                Assert.Equal(Now.AddDays(19), bonus.ResetsAt);
            });
    }

    [Fact]
    public void ParseMapsLegacyOutputAndInfersNextResetYear()
    {
        DateTimeOffset now = new(2026, 12, 30, 10, 0, 0, TimeSpan.FromHours(1));
        const string output = """
            | KIRO PRO                                           |
            ████████████████████████████████████████████████████ 80%
            (40.00 of 50 covered in plan), resets on 01/15
            Bonus credits: 5.00/10 credits used
            """;

        ProviderSnapshot snapshot = KiroUsageParser.Parse(output, now);

        Assert.Equal("KIRO PRO", snapshot.Identity?.Plan);
        Assert.Equal(80, snapshot.UsageWindows[0].UsedPercent);
        Assert.Equal(new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.FromHours(1)), snapshot.UsageWindows[0].ResetsAt);
        Assert.Null(snapshot.UsageWindows[1].ResetsAt);
    }

    [Fact]
    public void ParseUsesDisplayedPercentageWhenCreditCountsAreMissing()
    {
        const string output = """
            | KIRO FREE |
            ████████████████████ 25%
            """;

        UsageWindow window = Assert.Single(KiroUsageParser.Parse(output, Now).UsageWindows);

        Assert.Equal(25, window.UsedPercent);
    }

    [Fact]
    public void ParseKeepsManagedPlanIdentityWithoutInventingAQuota()
    {
        const string output = """
            Plan: Q Developer Pro
            Your plan is managed by admin
            """;

        ProviderSnapshot snapshot = KiroUsageParser.Parse(output, Now);

        Assert.Equal("Q Developer Pro", snapshot.Identity?.Plan);
        Assert.Empty(snapshot.UsageWindows);
    }

    [Fact]
    public void ParseClassifiesSignedOutOutput()
    {
        ProviderException exception = Assert.Throws<ProviderException>(() =>
            KiroUsageParser.Parse("Authentication required. Run kiro-cli login.", Now));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
    }

    [Fact]
    public void ParseRejectsIncompleteUsageOutput()
    {
        ProviderException exception = Assert.Throws<ProviderException>(() =>
            KiroUsageParser.Parse("Plan: loading...", Now));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
    }
}
