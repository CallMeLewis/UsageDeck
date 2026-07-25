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
}
