using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers.KimiCode;

namespace UsageDeck.Infrastructure.Tests;

public sealed class KimiCodeUsageParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 0, 5, 0, TimeSpan.Zero);

    [Fact]
    public void ParsesTheWindowsKimiCodeReports()
    {
        // Shape captured from the live service, including the legacy limits array it still sends.
        const string Json = """
            {
              "limits": [
                {
                  "window": { "duration": 300, "timeUnit": "TIME_UNIT_MINUTE" },
                  "detail": { "limit": "100", "used": "43", "remaining": "57", "resetTime": "2026-11-05T00:41:27.557059Z" }
                }
              ],
              "usages": {
                "limit_5h": { "used_ratio": 0.4326, "reset_time": "2026-11-05T00:41:27Z" },
                "limit_month_total": { "used_ratio": 0.0679, "reset_time": "2026-12-05T00:00:00Z" },
                "limit_month_code": { "used_ratio": 0.0518, "reset_time": "2026-12-05T00:00:00Z" }
              }
            }
            """;

        ProviderSnapshot snapshot = KimiCodeUsageParser.Parse(Encoding.UTF8.GetBytes(Json), Now, "Pro");

        Assert.Equal(ProviderId.KimiCode, snapshot.ProviderId);
        Assert.Equal(UsageDataState.Fresh, snapshot.State);
        Assert.Equal(Now, snapshot.CapturedAt);
        Assert.Equal("Kimi Code usage service", snapshot.SourceDescription);
        Assert.Equal(UsageDataCoverage.SignedInAccount, snapshot.Coverage);
        Assert.Equal("Pro", snapshot.Identity?.Plan);
        Assert.Null(snapshot.Identity?.Email);
        Assert.Collection(
            snapshot.UsageWindows,
            window =>
            {
                Assert.Equal("5-hour", window.Id);
                Assert.Equal("5-hour limit", window.DisplayName);
                Assert.Equal(43.26, window.UsedPercent, precision: 6);
                Assert.Equal(new DateTimeOffset(2026, 11, 5, 0, 41, 27, TimeSpan.Zero), window.ResetsAt);
                Assert.Equal(TimeSpan.FromHours(5), window.Duration);
            },
            window =>
            {
                Assert.Equal("monthly", window.Id);
                Assert.Equal("Monthly limit", window.DisplayName);
                Assert.Equal(6.79, window.UsedPercent, precision: 6);
                Assert.Equal(new DateTimeOffset(2026, 12, 5, 0, 0, 0, TimeSpan.Zero), window.ResetsAt);
            });
    }

    [Fact]
    public void IncludesTheWeeklyLimitInKimiCodesOrderAndAcceptsStringRatios()
    {
        const string Json = """
            {
              "usages": {
                "limit_month_total": { "used_ratio": "1.4" },
                "limit_7d": { "used_ratio": "0.25", "reset_time": "not a date" },
                "limit_5h": { "used_ratio": -0.2 }
              }
            }
            """;

        ProviderSnapshot snapshot = KimiCodeUsageParser.Parse(Encoding.UTF8.GetBytes(Json), Now);

        Assert.Equal(["5-hour", "weekly", "monthly"], snapshot.UsageWindows.Select(window => window.Id));
        Assert.Equal([0d, 25d, 100d], snapshot.UsageWindows.Select(window => window.UsedPercent));
        Assert.All(snapshot.UsageWindows, window => Assert.Null(window.ResetsAt));
        Assert.Null(snapshot.Identity);
    }

    [Fact]
    public void AnAccountWithoutLimitsReportsNoWindows()
    {
        ProviderSnapshot snapshot = KimiCodeUsageParser.Parse("""{ "usages": {} }"""u8, Now);

        Assert.Equal(UsageDataState.Fresh, snapshot.State);
        Assert.Empty(snapshot.UsageWindows);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{ "limits": [] }""")]
    [InlineData("""{ "usages": [] }""")]
    [InlineData("{ not json")]
    public void UnreadableResponsesAreReportedAsInvalid(string json)
    {
        ProviderException exception = Assert.Throws<ProviderException>(
            () => KimiCodeUsageParser.Parse(Encoding.UTF8.GetBytes(json), Now));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
    }

    [Theory]
    [InlineData("""{ "user_id": "u", "email": "someone@example.com", "user_level_name": " Pro " }""", "Pro")]
    [InlineData("""{ "user_level_name": "" }""", null)]
    [InlineData("""{ "user_level_name": 25 }""", null)]
    [InlineData("[]", null)]
    [InlineData("not json", null)]
    public void ParsePlanKeepsOnlyThePlanName(string json, string? expected)
    {
        Assert.Equal(expected, KimiCodeUsageParser.ParsePlan(Encoding.UTF8.GetBytes(json)));
    }
}
