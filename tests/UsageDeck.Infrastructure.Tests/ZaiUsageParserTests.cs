using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers.Zai;

namespace UsageDeck.Infrastructure.Tests;

public sealed class ZaiUsageParserTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseBuildsTokenAndMcpWindowsFromPersonalPlanLimits()
    {
        const string Json = """
            {
              "code": 200,
              "success": true,
              "data": {
                "planName": "Pro",
                "limits": [
                  {
                    "type": "TIME_LIMIT",
                    "unit": 5,
                    "number": 1,
                    "usage": 100,
                    "currentValue": 50,
                    "remaining": 50,
                    "percentage": 50
                  },
                  {
                    "type": "TOKENS_LIMIT",
                    "unit": 3,
                    "number": 5,
                    "usage": 40000000,
                    "currentValue": 13628365,
                    "remaining": 26371635,
                    "percentage": 34,
                    "nextResetTime": 1768507567547
                  }
                ]
              }
            }
            """;

        ProviderSnapshot snapshot = ZaiUsageParser.Parse(Encoding.UTF8.GetBytes(Json), CapturedAt);

        Assert.Equal(ProviderId.Zai, snapshot.ProviderId);
        Assert.Equal("Z.AI Coding Plan API", snapshot.SourceDescription);
        Assert.Equal("Pro", snapshot.Identity?.Plan);
        Assert.Equal(CapturedAt, snapshot.CapturedAt);
        Assert.Collection(
            snapshot.UsageWindows,
            window =>
            {
                Assert.Equal("5-hour", window.DisplayName);
                Assert.Equal(34.0709125, window.UsedPercent, 7);
                Assert.Equal(TimeSpan.FromHours(5), window.Duration);
                Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1768507567547), window.ResetsAt);
                Assert.Equal(UsageConfidence.Authoritative, window.Confidence);
            },
            window =>
            {
                Assert.Equal("MCP tools", window.DisplayName);
                Assert.Equal(50, window.UsedPercent);
                Assert.Null(window.Duration);
            });
    }

    [Fact]
    public void ParseOrdersTokenWindowsByDuration()
    {
        const string Json = """
            {
              "code": 200,
              "success": true,
              "data": {
                "limits": [
                  { "type": "TOKENS_LIMIT", "unit": 6, "number": 1, "percentage": 7 },
                  { "type": "TIME_LIMIT", "usage": 1000, "currentValue": 147, "remaining": 853 },
                  { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 8 }
                ]
              }
            }
            """;

        ProviderSnapshot snapshot = ZaiUsageParser.Parse(Encoding.UTF8.GetBytes(Json), CapturedAt);

        Assert.Equal(["5-hour", "Weekly", "MCP tools"], snapshot.UsageWindows.Select(window => window.DisplayName));
        Assert.Equal([8d, 7d, 14.7d], snapshot.UsageWindows.Select(window => window.UsedPercent));
    }

    [Fact]
    public void ParseMapsAuthenticationFailureWithoutExposingTheServerMessage()
    {
        const string Json = """
            { "code": 1001, "success": false, "msg": "token secret-value was rejected" }
            """;

        ProviderException exception = Assert.Throws<ProviderException>(
            () => ZaiUsageParser.Parse(Encoding.UTF8.GetBytes(Json), CapturedAt));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Category);
        Assert.DoesNotContain("secret-value", exception.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsMalformedJsonWithASafeError()
    {
        ProviderException exception = Assert.Throws<ProviderException>(
            () => ZaiUsageParser.Parse("not-json"u8, CapturedAt));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
        Assert.DoesNotContain("not-json", exception.SafeMessage, StringComparison.Ordinal);
    }
}
