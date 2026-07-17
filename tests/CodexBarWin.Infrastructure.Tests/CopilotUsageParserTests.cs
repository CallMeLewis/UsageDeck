using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Providers.Copilot;

namespace CodexBarWin.Infrastructure.Tests;

public sealed class CopilotUsageParserTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseMapsMeteredAndUnlimitedQuotaSnapshots()
    {
        const string json = """
            {
              "copilot_plan": "pro",
              "quota_reset_date_utc": "2026-08-01T00:00:00Z",
              "quota_snapshots": {
                "premium_interactions": {
                  "has_quota": true,
                  "percent_remaining": 80,
                  "entitlement": 300,
                  "remaining": 240,
                  "unlimited": false
                },
                "chat": {
                  "has_quota": true,
                  "unlimited": true
                },
                "completions": {
                  "has_quota": false,
                  "percent_remaining": 0,
                  "unlimited": false
                }
              }
            }
            """;

        ProviderSnapshot snapshot = CopilotUsageParser.Parse(json, CapturedAt);

        Assert.Equal("pro", snapshot.Identity?.Plan);
        Assert.Collection(
            snapshot.UsageWindows,
            premium =>
            {
                Assert.Equal("premium_interactions", premium.Id);
                Assert.Equal(20, premium.UsedPercent);
                Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), premium.ResetsAt);
            },
            chat =>
            {
                Assert.Equal("chat", chat.Id);
                Assert.True(chat.IsUnlimited);
            });
    }

    [Fact]
    public void ParseMapsLimitedFreePlanQuotas()
    {
        const string json = """
            {
              "copilot_plan": "free",
              "limited_user_reset_date": "2026-08-01",
              "limited_user_quotas": { "chat": 40, "completions": 800 },
              "monthly_quotas": { "chat": 50, "completions": 2000 }
            }
            """;

        ProviderSnapshot snapshot = CopilotUsageParser.Parse(json, CapturedAt);

        Assert.Collection(
            snapshot.UsageWindows,
            chat => Assert.Equal(20, chat.UsedPercent),
            completions => Assert.Equal(60, completions.UsedPercent));
    }

    [Fact]
    public void ParseClassifiesAuthenticationErrors()
    {
        ProviderException exception = Assert.Throws<ProviderException>(() =>
            CopilotUsageParser.Parse("{\"message\":\"Bad credentials\"}", CapturedAt));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
    }
}
