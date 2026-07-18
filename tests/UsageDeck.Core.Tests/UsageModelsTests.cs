using UsageDeck.Core.Providers;

namespace UsageDeck.Core.Tests;

public sealed class UsageModelsTests
{
    [Theory]
    [InlineData(-12, 0)]
    [InlineData(0, 0)]
    [InlineData(48.5, 48.5)]
    [InlineData(120, 100)]
    public void UsageWindowClampsPercentage(double input, double expected)
    {
        UsageWindow window = new("session", "Session", input);

        Assert.Equal(expected, window.UsedPercent);
        Assert.Equal(100 - expected, window.RemainingPercent);
    }

    [Fact]
    public void UsageWindowRejectsNonFinitePercentage()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new UsageWindow("session", "Session", double.NaN));
    }

    [Fact]
    public void ProviderIdNormalisesKnownSafeCharacters()
    {
        ProviderId id = new(" Codex-Spark_Weekly ");

        Assert.Equal("codex-spark_weekly", id.Value);
    }

    [Fact]
    public void ProviderIdRejectsPathCharacters()
    {
        Assert.Throws<ArgumentException>(() => new ProviderId("../codex"));
    }

    [Fact]
    public void SupportedProvidersHaveUserFacingNames()
    {
        Assert.Equal(
            ["OpenAI Codex", "Claude", "Antigravity", "GitHub Copilot", "Kiro", "Amp", "OpenCode Go", "Z.AI"],
            ProviderId.Supported.Select(provider => provider.DisplayName));
    }

    [Fact]
    public void ResetCreditsRejectNegativeAvailability()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitResetCredits(-1));
    }

    [Fact]
    public void ProviderSnapshotPreservesCliVersionWhenDataBecomesStale()
    {
        ProviderSnapshot snapshot = new(
            ProviderId.Codex,
            "Codex",
            "Native CLI",
            DateTimeOffset.UtcNow,
            UsageDataState.Fresh,
            cliVersion: "0.144.5");

        ProviderSnapshot stale = snapshot.WithFailure(UsageDataState.Stale, "Refresh failed.");

        Assert.Equal("0.144.5", stale.CliVersion);
    }
}
