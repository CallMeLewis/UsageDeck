using System.Globalization;
using System.Text;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers.TheClawBay;

namespace UsageDeck.Infrastructure.Tests;

public sealed class TheClawBayUsageParserTests
{
    private const string QuotaJson = """
        {
          "observedAt": "2026-07-31T16:00:00Z",
          "usage": {
            "fiveHour": {
              "progressPercentUsed": 27.5,
              "percentUsed": 25,
              "windowEnd": "2026-07-31T20:00:00Z",
              "secondsUntilReset": 14400
            },
            "weekly": {
              "percentUsed": 63,
              "windowEnd": "2026-08-03T00:00:00Z",
              "secondsUntilReset": 288000
            }
          }
        }
        """;

    [Fact]
    public void ParseBuildsBothAuthoritativeWindows()
    {
        ProviderSnapshot snapshot = TheClawBayUsageParser.Parse(
            Encoding.UTF8.GetBytes(QuotaJson),
            "TheClawBay API");

        Assert.Equal(ProviderId.TheClawBay, snapshot.ProviderId);
        Assert.Equal("TheClawBay API", snapshot.SourceDescription);
        Assert.Equal(new DateTimeOffset(2026, 7, 31, 16, 0, 0, TimeSpan.Zero), snapshot.CapturedAt);
        Assert.Collection(
            snapshot.UsageWindows,
            window =>
            {
                Assert.Equal("five-hour", window.Id);
                Assert.Equal("5-hour", window.DisplayName);
                Assert.Equal(27.5, window.UsedPercent);
                Assert.Equal(new DateTimeOffset(2026, 7, 31, 20, 0, 0, TimeSpan.Zero), window.ResetsAt);
                Assert.Equal(TimeSpan.FromHours(5), window.Duration);
                Assert.Equal(UsageConfidence.Authoritative, window.Confidence);
            },
            window =>
            {
                Assert.Equal("weekly", window.Id);
                Assert.Equal("Weekly", window.DisplayName);
                Assert.Equal(63, window.UsedPercent);
                Assert.Equal(new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero), window.ResetsAt);
                Assert.Equal(TimeSpan.FromDays(7), window.Duration);
            });
    }

    [Fact]
    public void ParsePrefersWindowEndOverSecondsUntilReset()
    {
        const string Json = """
            {
              "observedAt": "2026-07-31T16:00:00Z",
              "usage": {
                "fiveHour": {
                  "percentUsed": 10,
                  "windowEnd": "2026-07-31T17:00:00Z",
                  "secondsUntilReset": 7200
                },
                "weekly": {
                  "percentUsed": 20,
                  "windowEnd": "2026-08-03T00:00:00Z"
                }
              }
            }
            """;

        ProviderSnapshot snapshot = TheClawBayUsageParser.Parse(Encoding.UTF8.GetBytes(Json), "TheClawBay API");

        Assert.Equal(new DateTimeOffset(2026, 7, 31, 17, 0, 0, TimeSpan.Zero), snapshot.UsageWindows[0].ResetsAt);
    }

    [Fact]
    public void ParseUsesObservedAtAndSecondsUntilResetWhenWindowEndIsAbsent()
    {
        const string Json = """
            {
              "observedAt": "2026-07-31T16:00:00Z",
              "usage": {
                "fiveHour": {
                  "percentUsed": 10,
                  "secondsUntilReset": 5400
                },
                "weekly": {
                  "percentUsed": 20,
                  "secondsUntilReset": 201600
                }
              }
            }
            """;

        ProviderSnapshot snapshot = TheClawBayUsageParser.Parse(Encoding.UTF8.GetBytes(Json), "TheClawBay API");

        Assert.Equal(new DateTimeOffset(2026, 7, 31, 17, 30, 0, TimeSpan.Zero), snapshot.UsageWindows[0].ResetsAt);
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero), snapshot.UsageWindows[1].ResetsAt);
    }

    [Theory]
    [InlineData(-1000)]
    [InlineData(1000)]
    [InlineData(-0.0101)]
    [InlineData(100.0101)]
    public void ParseRejectsOutOfRangePreferredPercentageWithoutFallingBack(double invalidPercentage)
    {
        string json = $$"""
            {
              "observedAt": "2026-07-31T16:00:00Z",
              "usage": {
                "fiveHour": {
                  "progressPercentUsed": {{invalidPercentage.ToString(CultureInfo.InvariantCulture)}},
                  "percentUsed": 25,
                  "windowEnd": "2026-07-31T20:00:00Z"
                },
                "weekly": { "percentUsed": 63, "windowEnd": "2026-08-03T00:00:00Z" }
              }
            }
            """;

        ProviderException exception = Assert.Throws<ProviderException>(
            () => TheClawBayUsageParser.Parse(Encoding.UTF8.GetBytes(json), "TheClawBay API"));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
    }

    [Fact]
    public void ParseRejectsMalformedPreferredPercentageWithoutFallingBack()
    {
        const string Json = """
            {
              "observedAt": "2026-07-31T16:00:00Z",
              "usage": {
                "fiveHour": {
                  "progressPercentUsed": "not-a-percentage",
                  "percentUsed": 25,
                  "windowEnd": "2026-07-31T20:00:00Z"
                },
                "weekly": { "percentUsed": 63, "windowEnd": "2026-08-03T00:00:00Z" }
              }
            }
            """;

        ProviderException exception = Assert.Throws<ProviderException>(
            () => TheClawBayUsageParser.Parse(Encoding.UTF8.GetBytes(Json), "TheClawBay API"));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
    }

    [Theory]
    [InlineData(-1000)]
    [InlineData(1000)]
    public void ParseRejectsOutOfRangeFallbackPercentage(double invalidPercentage)
    {
        string json = $$"""
            {
              "observedAt": "2026-07-31T16:00:00Z",
              "usage": {
                "fiveHour": {
                  "percentUsed": {{invalidPercentage.ToString(CultureInfo.InvariantCulture)}},
                  "windowEnd": "2026-07-31T20:00:00Z"
                },
                "weekly": { "percentUsed": 63, "windowEnd": "2026-08-03T00:00:00Z" }
              }
            }
            """;

        ProviderException exception = Assert.Throws<ProviderException>(
            () => TheClawBayUsageParser.Parse(Encoding.UTF8.GetBytes(json), "TheClawBay API"));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
    }

    [Fact]
    public void ParseClampsOnlyBoundaryDriftWithinOneHundredthOfAPercentagePoint()
    {
        const string Json = """
            {
              "observedAt": "2026-07-31T16:00:00Z",
              "usage": {
                "fiveHour": { "percentUsed": -0.01, "windowEnd": "2026-07-31T20:00:00Z" },
                "weekly": { "percentUsed": 100.01, "windowEnd": "2026-08-03T00:00:00Z" }
              }
            }
            """;

        ProviderSnapshot snapshot = TheClawBayUsageParser.Parse(
            Encoding.UTF8.GetBytes(Json),
            "TheClawBay API");

        Assert.Equal(0, snapshot.UsageWindows[0].UsedPercent);
        Assert.Equal(100, snapshot.UsageWindows[1].UsedPercent);
    }

    [Theory]
    [MemberData(nameof(InvalidQuotaResponses))]
    public void ParseRejectsInvalidQuotaResponsesWithoutExposingThePayload(string json)
    {
        ProviderException exception = Assert.Throws<ProviderException>(
            () => TheClawBayUsageParser.Parse(Encoding.UTF8.GetBytes(json), "TheClawBay API"));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
        if (!string.IsNullOrEmpty(json))
        {
            Assert.DoesNotContain(json, exception.SafeMessage, StringComparison.Ordinal);
        }
    }

    public static IEnumerable<object[]> InvalidQuotaResponses()
    {
        yield return ["""
            { "usage": { "fiveHour": { "percentUsed": 10, "windowEnd": "2026-07-31T20:00:00Z" }, "weekly": { "percentUsed": 20, "windowEnd": "2026-08-03T00:00:00Z" } }, "secret": "missing-observed-at" }
            """];
        yield return ["""
            { "observedAt": "2026-07-31T16:00:00Z", "usage": { "weekly": { "percentUsed": 20, "windowEnd": "2026-08-03T00:00:00Z" } }, "secret": "missing-five-hour" }
            """];
        yield return ["""
            { "observedAt": "2026-07-31T16:00:00Z", "usage": { "fiveHour": { "percentUsed": 10, "windowEnd": "2026-07-31T20:00:00Z" } }, "secret": "missing-weekly" }
            """];
        yield return ["""
            { "observedAt": "2026-07-31T16:00:00Z", "usage": { "fiveHour": { "windowEnd": "2026-07-31T20:00:00Z" }, "weekly": { "percentUsed": 20, "windowEnd": "2026-08-03T00:00:00Z" } }, "secret": "missing-percentage" }
            """];
        yield return ["""
            { "observedAt": "2026-07-31T16:00:00Z", "usage": { "fiveHour": { "progressPercentUsed": "ten", "percentUsed": "also-ten", "windowEnd": "2026-07-31T20:00:00Z" }, "weekly": { "percentUsed": 20, "windowEnd": "2026-08-03T00:00:00Z" } }, "secret": "non-numeric-percentage" }
            """];
        yield return ["""
            { "observedAt": "2026-07-31T16:00:00Z", "usage": { "fiveHour": { "percentUsed": 1e999, "windowEnd": "2026-07-31T20:00:00Z" }, "weekly": { "percentUsed": 20, "windowEnd": "2026-08-03T00:00:00Z" } }, "secret": "non-finite-percentage" }
            """];
        yield return ["""
            { "observedAt": "2026-07-31T16:00:00Z", "usage": { "fiveHour": { "percentUsed": 10 }, "weekly": { "percentUsed": 20, "windowEnd": "2026-08-03T00:00:00Z" } }, "secret": "missing-reset" }
            """];
        yield return ["""
            { "observedAt": "not-a-timestamp", "usage": { "fiveHour": { "percentUsed": 10, "windowEnd": "2026-07-31T20:00:00Z" }, "weekly": { "percentUsed": 20, "windowEnd": "2026-08-03T00:00:00Z" } }, "secret": "invalid-observed-at" }
            """];
        yield return ["""
            { "observedAt": "2026-07-31T16:00:00Z", "usage": { "fiveHour": { "percentUsed": 10, "windowEnd": "not-a-timestamp" }, "weekly": { "percentUsed": 20, "windowEnd": "2026-08-03T00:00:00Z" } }, "secret": "invalid-window-end" }
            """];
        yield return ["""
            { "observedAt": "2026-07-31T16:00:00Z", "usage": { "fiveHour": { "percentUsed": 10, "secondsUntilReset": -1 }, "weekly": { "percentUsed": 20, "windowEnd": "2026-08-03T00:00:00Z" } }, "secret": "negative-reset-seconds" }
            """];
        yield return ["""
            { "observedAt": "2026-07-31T16:00:00Z", "usage": { "fiveHour": { "percentUsed": 10, "secondsUntilReset": 1e20 }, "weekly": { "percentUsed": 20, "windowEnd": "2026-08-03T00:00:00Z" } }, "secret": "oversized-reset-seconds" }
            """];
        yield return ["[ { \"secret\": \"root-array\" } ]"];
        yield return ["{ \"secret\": \"malformed-json\""];
        yield return [CreateTooDeepJson()];
        yield return [string.Empty];
    }

    private static string CreateTooDeepJson()
    {
        const string Opening = "{ \"next\": ";
        const string Closing = " }";
        return string.Concat(Enumerable.Repeat(Opening, 33))
            + "{ \"secret\": \"too-deep\" }"
            + string.Concat(Enumerable.Repeat(Closing, 33));
    }
}
