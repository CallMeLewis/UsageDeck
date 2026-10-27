using UsageDeck.Core.Providers;

namespace UsageDeck.App.Tests;

public sealed class UsageResetRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RefreshesOnceShortlyAfterTheEarliestReset()
    {
        UsageResetRefresh resets = new();
        resets.Record(
            Snapshot(
                ProviderId.Codex,
                new UsageWindow("weekly", "Weekly", 40, Now.AddDays(3)),
                new UsageWindow("five-hour", "5-hour", 100, Now.AddMinutes(10))),
            Now);

        Assert.Empty(resets.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(10)));
        Assert.Equal(
            [ProviderId.Codex],
            resets.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(10).Add(UsageResetRefresh.ResetGracePeriod)));
        Assert.Empty(resets.TakeDueProviders([ProviderId.Codex], Now.AddHours(1)));
    }

    [Fact]
    public void IgnoresPastUnlimitedAndMissingResetTimes()
    {
        UsageResetRefresh resets = new();
        resets.Record(
            Snapshot(
                ProviderId.Claude,
                new UsageWindow("session", "Session", 100, Now.AddMinutes(-1)),
                new UsageWindow("unlimited", "Unlimited", 0, Now.AddMinutes(5), isUnlimited: true),
                new UsageWindow("weekly", "Weekly", 20)),
            Now);

        Assert.Empty(resets.TakeDueProviders([ProviderId.Claude], Now.AddDays(30)));
    }

    [Fact]
    public void ANewerReadingReplacesTheScheduledRefresh()
    {
        UsageResetRefresh resets = new();
        resets.Record(Snapshot(ProviderId.Kiro, new UsageWindow("month", "Monthly", 90, Now.AddMinutes(5))), Now);
        resets.Record(Snapshot(ProviderId.Kiro, new UsageWindow("month", "Monthly", 0, Now.AddDays(30))), Now.AddMinutes(6));

        Assert.Empty(resets.TakeDueProviders([ProviderId.Kiro], Now.AddMinutes(10)));
    }

    [Fact]
    public void DisabledProvidersLoseTheirScheduledRefresh()
    {
        UsageResetRefresh resets = new();
        resets.Record(Snapshot(ProviderId.Amp, new UsageWindow("daily", "Daily", 80, Now.AddMinutes(1))), Now);

        Assert.Empty(resets.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(1)));
        Assert.Empty(resets.TakeDueProviders([ProviderId.Amp], Now.AddMinutes(5)));
    }

    private static ProviderSnapshot Snapshot(ProviderId provider, params UsageWindow[] windows) =>
        new(provider, provider.DisplayName, "Test source", Now, UsageDataState.Fresh, windows);
}
