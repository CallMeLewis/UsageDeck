using System.Text;
using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Providers.OpenCodeGo;
using CodexBarWin.Infrastructure.Settings;

namespace CodexBarWin.Infrastructure.Tests;

public sealed class OpenCodeGoUsageExportParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParsesManagedInferenceCostsIntoThirtyDayEstimates()
    {
        byte[] csv = Csv(
            "1,user@example.com,,\"Editor, Windows\",opencode,glm-5,0,0,0,0,0,0,,,,managed-inference,300000000,2026-07-18T11:00:00Z",
            "2,user@example.com,,app,opencode,kimi-k2.6,0,0,0,0,0,0,,,,managed-inference,600000000,2026-07-16T12:00:00Z",
            "3,user@example.com,,app,opencode,minimax-m2.5,0,0,0,0,0,0,,,,managed-inference,300000000,2026-07-08T12:00:00Z",
            "4,user@example.com,,app,anthropic,claude,0,0,0,0,0,0,,,,byok,900000000,2026-07-18T11:30:00Z");

        ProviderSnapshot snapshot = OpenCodeGoUsageExportParser.Parse(
            csv,
            Now,
            OpenCodeGoUsageRange.ThirtyDays);

        Assert.Equal("OpenCode Console API billing", snapshot.SourceDescription);
        Assert.Collection(
            snapshot.UsageWindows,
            window => AssertWindow(window, "5-hour", 25),
            window => AssertWindow(window, "7-day", 30),
            window => AssertWindow(window, "30-day", 20));
        Assert.All(snapshot.UsageWindows, window => Assert.Equal(UsageConfidence.Estimated, window.Confidence));
    }

    [Theory]
    [InlineData(OpenCodeGoUsageRange.OneDay, 1)]
    [InlineData(OpenCodeGoUsageRange.SevenDays, 2)]
    [InlineData(OpenCodeGoUsageRange.ThirtyDays, 3)]
    public void RangeControlsTheEstimatedWindows(OpenCodeGoUsageRange range, int expectedWindows)
    {
        ProviderSnapshot snapshot = OpenCodeGoUsageExportParser.Parse(Csv(), Now, range);

        Assert.Equal(expectedWindows, snapshot.UsageWindows.Count);
        Assert.All(snapshot.UsageWindows, window => Assert.Equal(0, window.UsedPercent));
    }

    [Fact]
    public void RejectsMissingBillingColumns()
    {
        byte[] csv = Encoding.UTF8.GetBytes("id,created_at\n1,2026-07-18T11:00:00Z");

        ProviderException exception = Assert.Throws<ProviderException>(() =>
            OpenCodeGoUsageExportParser.Parse(csv, Now, OpenCodeGoUsageRange.ThirtyDays));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
    }

    private static byte[] Csv(params string[] rows)
    {
        const string Header = "id,user_email,service_account_name,app,provider,model,input_tokens,output_tokens,reasoning_tokens,cache_read_tokens,cache_write_5m_tokens,cache_write_1h_tokens,reasoning_mode,reasoning_effort,reasoning_budget_tokens,billing_source,cost_micro_cents,created_at";
        return Encoding.UTF8.GetBytes(string.Join('\n', new[] { Header }.Concat(rows)));
    }

    private static void AssertWindow(UsageWindow window, string displayName, double usedPercent)
    {
        Assert.Equal(displayName, window.DisplayName);
        Assert.Equal(usedPercent, window.UsedPercent);
    }
}
