using System.Globalization;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers.Claude;

namespace UsageDeck.Infrastructure.Tests;

public sealed class ClaudeApiUsageParserTests
{
    // Trimmed from a real response of the OAuth usage endpoint the Claude Code CLI draws its
    // /usage panel from. Fields UsageDeck does not read are kept where they document the shape.
    private const string RealResponse = """
        {
          "five_hour": { "utilization": 29.0, "resets_at": "2026-07-26T02:40:00.373078+01:00" },
          "seven_day": { "utilization": 3.0, "resets_at": "2026-07-29T04:00:00.373099+01:00" },
          "seven_day_opus": null,
          "limits": [
            {
              "kind": "session",
              "group": "session",
              "percent": 29,
              "severity": "normal",
              "resets_at": "2026-07-26T02:40:00.373078+01:00",
              "scope": null,
              "is_active": true
            },
            {
              "kind": "weekly_all",
              "group": "weekly",
              "percent": 3,
              "severity": "normal",
              "resets_at": "2026-07-29T04:00:00.373099+01:00",
              "scope": null,
              "is_active": false
            },
            {
              "kind": "weekly_scoped",
              "group": "weekly",
              "percent": 2,
              "severity": "normal",
              "resets_at": "2026-07-29T04:00:00.373321+01:00",
              "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null },
              "is_active": false
            }
          ]
        }
        """;

    [Fact]
    public void ParseMapsLimitsToTheSameWindowsTheCliPanelProduces()
    {
        IReadOnlyList<UsageWindow> windows = ClaudeApiUsageParser.Parse(RealResponse);

        Assert.Collection(
            windows,
            session =>
            {
                Assert.Equal("session", session.Id);
                Assert.Equal("Current session", session.DisplayName);
                Assert.Equal(29, session.UsedPercent);
                Assert.Equal(
                    DateTimeOffset.Parse("2026-07-26T02:40:00.373078+01:00", CultureInfo.InvariantCulture),
                    session.ResetsAt);
            },
            weekly =>
            {
                Assert.Equal("weekly", weekly.Id);
                Assert.Equal("Weekly limit", weekly.DisplayName);
                Assert.Equal(3, weekly.UsedPercent);
            },
            fable =>
            {
                Assert.Equal("weekly-fable", fable.Id);
                Assert.Equal("Fable weekly", fable.DisplayName);
                Assert.Equal(2, fable.UsedPercent);
                Assert.Equal(
                    DateTimeOffset.Parse("2026-07-29T04:00:00.373321+01:00", CultureInfo.InvariantCulture),
                    fable.ResetsAt);
            });
    }

    [Fact]
    public void ParseIncludesInactiveLimitsBecauseTheCliPanelShowsThemToo()
    {
        IReadOnlyList<UsageWindow> windows = ClaudeApiUsageParser.Parse(RealResponse);

        Assert.Equal(3, windows.Count);
    }

    [Fact]
    public void ParseSkipsUnknownLimitKindsInsteadOfFailing()
    {
        const string json = """
            {
              "limits": [
                { "kind": "session", "percent": 10, "resets_at": "2026-07-26T02:40:00+01:00" },
                { "kind": "daily_novel_thing", "percent": 50, "resets_at": "2026-07-26T02:40:00+01:00" }
              ]
            }
            """;

        UsageWindow window = Assert.Single(ClaudeApiUsageParser.Parse(json));

        Assert.Equal("session", window.Id);
    }

    [Fact]
    public void ParseToleratesAScopedLimitWithoutAModelName()
    {
        const string json = """
            {
              "limits": [
                { "kind": "session", "percent": 10, "resets_at": "2026-07-26T02:40:00+01:00" },
                { "kind": "weekly_scoped", "percent": 5, "resets_at": "2026-07-29T04:00:00+01:00", "scope": null }
              ]
            }
            """;

        IReadOnlyList<UsageWindow> windows = ClaudeApiUsageParser.Parse(json);

        Assert.Equal("weekly-model", windows[1].Id);
        Assert.Equal("Weekly model limit", windows[1].DisplayName);
    }

    [Fact]
    public void ParseToleratesAMissingResetTime()
    {
        const string json = """
            { "limits": [ { "kind": "session", "percent": 10, "resets_at": null } ] }
            """;

        UsageWindow window = Assert.Single(ClaudeApiUsageParser.Parse(json));

        Assert.Null(window.ResetsAt);
    }

    [Theory]
    [InlineData("""{ "five_hour": { "utilization": 1.0 } }""")]
    [InlineData("""{ "limits": [] }""")]
    [InlineData("""{ "limits": [ { "kind": "weekly_all", "percent": 3 } ] }""")]
    [InlineData("not json at all")]
    public void ParseRejectsResponsesWithoutASessionLimit(string json)
    {
        ProviderException exception = Assert.Throws<ProviderException>(() => ClaudeApiUsageParser.Parse(json));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
    }

    // Trimmed from a real resets block, returned when the request asks for cedar_ember and
    // identifies as a current Claude Code CLI. Opaque ids are replaced.
    private const string ResetsResponse = """
        {
          "limits": [ { "kind": "session", "percent": 7, "resets_at": "2026-09-23T20:00:00+00:00" } ],
          "cedar_ember": {
            "eligible": true,
            "ineligible_reason": null,
            "at_limit": false,
            "exhausted": [],
            "grants": [
              {
                "id": "grant-a",
                "resets_total": 2,
                "resets_left": 2,
                "starts_at": "2026-09-22T16:00:00+00:00",
                "ends_at": "2026-10-22T16:00:00+00:00",
                "clears": ["five_hour", "seven_day", "seven_day_overage_included"],
                "paused": false,
                "usable_now": true,
                "use_requires_limit": false
              },
              { "id": "grant-b", "resets_total": 1, "resets_left": 0, "ends_at": "2026-10-01T00:00:00+00:00" },
              { "id": "grant-c", "resets_total": 1, "resets_left": 1, "ends_at": null }
            ],
            "next_grant_id": "grant-a",
            "weekly_resets_at": "2026-09-30T03:00:00+00:00",
            "cooldown_until": null
          }
        }
        """;

    [Fact]
    public void ParseResetCreditsListsEachRemainingResetWithItsDeadline()
    {
        RateLimitResetCredits? credits = ClaudeApiUsageParser.ParseResetCredits(ResetsResponse);

        Assert.NotNull(credits);
        Assert.Equal(3, credits.AvailableCount);
        DateTimeOffset deadline = new(2026, 10, 22, 16, 0, 0, TimeSpan.Zero);
        Assert.Equal([deadline, deadline, null], credits.Credits.Select(credit => credit.ExpiresAt));
    }

    [Fact]
    public void ParseResetCreditsReportsNoneWhenAnEligibleAccountHasUsedEveryReset()
    {
        const string json = """
            { "limits": [], "cedar_ember": { "eligible": true, "grants": [ { "id": "a", "resets_left": 0 } ] } }
            """;

        RateLimitResetCredits? credits = ClaudeApiUsageParser.ParseResetCredits(json);

        Assert.Equal(0, credits?.AvailableCount);
    }

    [Theory]
    [InlineData("""{ "limits": [] }""")]
    [InlineData("""{ "limits": [], "cedar_ember": null }""")]
    [InlineData("""{ "limits": [], "cedar_ember": { "eligible": false, "ineligible_reason": "surface", "grants": [] } }""")]
    [InlineData("""{ "limits": [], "cedar_ember": { "eligible": true } }""")]
    public void ParseResetCreditsIsUnknownWhenTheAccountIsIneligibleOrTheBlockIsMissing(string json)
    {
        Assert.Null(ClaudeApiUsageParser.ParseResetCredits(json));
    }
}
