using UsageDeck.Core.Providers;

namespace UsageDeck.App.Tests;

public sealed class UsageFailureConfirmationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FailureGetsOnlyTwoConfirmationChecksUntilRecovery()
    {
        UsageFailureConfirmation confirmation = new();
        confirmation.Record(Failure(), Now);
        Assert.Empty(confirmation.TakeDueProviders([ProviderId.Codex], Now.AddSeconds(59)));
        Assert.Equal([ProviderId.Codex], confirmation.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(1)));
        Assert.Empty(confirmation.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(1)));
        confirmation.Record(Failure(), Now.AddMinutes(1));
        Assert.Equal([ProviderId.Codex], confirmation.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(2)));
        confirmation.Record(Failure(), Now.AddMinutes(2));
        Assert.Empty(confirmation.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(30)));
        confirmation.Record(Failure(), Now.AddMinutes(30));
        Assert.Empty(confirmation.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(31)));

        confirmation.Record(Fresh(), Now.AddMinutes(32));
        confirmation.Record(Failure(), Now.AddMinutes(33));
        Assert.Equal([ProviderId.Codex], confirmation.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(34)));
    }

    [Fact]
    public void OrdinaryRefreshCountsTowardsConfirmationAndReschedulesFromItsCompletion()
    {
        UsageFailureConfirmation confirmation = new();
        confirmation.Record(Failure(), Now);
        confirmation.Record(Failure(), Now.AddSeconds(50));
        Assert.Empty(confirmation.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(1)));
        Assert.Equal([ProviderId.Codex], confirmation.TakeDueProviders([ProviderId.Codex], Now.AddSeconds(110)));
        confirmation.Record(Failure(), Now.AddSeconds(110));
        Assert.Empty(confirmation.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(5)));
    }

    [Theory]
    [InlineData(ProviderErrorCategory.AuthenticationRequired)]
    [InlineData(ProviderErrorCategory.NotInstalled)]
    public void ActionableSetupFailuresDoNotScheduleConfirmation(ProviderErrorCategory category)
    {
        UsageFailureConfirmation confirmation = new();
        confirmation.Record(Failure(category), Now);
        Assert.Empty(confirmation.TakeDueProviders([ProviderId.Codex], Now.AddHours(1)));
    }

    [Fact]
    public void RecoveryAndDisabledProvidersCancelPendingChecks()
    {
        UsageFailureConfirmation confirmation = new();
        confirmation.Record(Failure(), Now);
        confirmation.Record(Fresh(), Now.AddSeconds(30));
        Assert.Empty(confirmation.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(1)));
        confirmation.Record(Failure(), Now.AddMinutes(2));
        Assert.Empty(confirmation.TakeDueProviders([ProviderId.Claude], Now.AddMinutes(3)));
        Assert.Empty(confirmation.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(4)));
    }

    [Fact]
    public void ProviderBackoffBlocksRefreshAndDelaysConfirmation()
    {
        UsageFailureConfirmation confirmation = new();
        confirmation.Record(Failure() with { RetryNotBeforeUtc = Now.AddMinutes(10) }, Now);
        Assert.False(confirmation.CanRefresh(ProviderId.Codex, Now.AddMinutes(9)));
        Assert.True(confirmation.CanRefresh(ProviderId.Claude, Now));
        Assert.Empty(confirmation.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(9)));
        Assert.True(confirmation.CanRefresh(ProviderId.Codex, Now.AddMinutes(10)));
        Assert.Equal([ProviderId.Codex], confirmation.TakeDueProviders([ProviderId.Codex], Now.AddMinutes(10)));
    }

    private static ProviderSnapshot Fresh() => new(ProviderId.Codex, "Codex", "Test", Now, UsageDataState.Fresh);

    private static ProviderSnapshot Failure(ProviderErrorCategory category = ProviderErrorCategory.Transient) =>
        Fresh().WithFailure(UsageDataState.Stale, "Usage could not be refreshed.", category);
}
