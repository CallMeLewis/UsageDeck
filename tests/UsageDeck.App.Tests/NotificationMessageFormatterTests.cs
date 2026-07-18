using UsageDeck.Core.Notifications;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App.Tests;

public sealed class NotificationMessageFormatterTests
{
    [Theory]
    [InlineData(UsageValueDisplayMode.Used, "84% used")]
    [InlineData(UsageValueDisplayMode.Remaining, "16% remaining")]
    public void ThresholdMessageFollowsUsageDisplayPreference(
        UsageValueDisplayMode displayMode,
        string expected)
    {
        LimitThresholdCrossedNotification notification = new(
            ProviderId.Codex,
            "Codex",
            "five-hour",
            "5-hour",
            84,
            20);

        NotificationMessage message = NotificationMessageFormatter.Format(notification, displayMode);

        Assert.Equal("Codex limit warning", message.Title);
        Assert.Contains(expected, message.Body, StringComparison.Ordinal);
        Assert.Equal(ProviderId.Codex, message.ProviderId);
    }

    [Fact]
    public void ExhaustedThresholdUsesClearLimitReachedWording()
    {
        LimitThresholdCrossedNotification notification = new(
            ProviderId.Claude,
            "Claude Code",
            "five-hour",
            "5-hour",
            100,
            0);

        NotificationMessage message = NotificationMessageFormatter.Format(
            notification,
            UsageValueDisplayMode.Remaining);

        Assert.Equal("Claude Code limit reached", message.Title);
        Assert.Equal("The 5-hour allowance is exhausted.", message.Body);
    }

    [Fact]
    public void IncidentMessageIncludesOfficialIncidentAction()
    {
        Uri incidentUri = new("https://status.example.com/incidents/1");
        ProviderIncidentDetectedNotification notification = new(
            ProviderId.Copilot,
            "GitHub Copilot",
            "Elevated error rates.",
            incidentUri);

        NotificationMessage message = NotificationMessageFormatter.Format(
            notification,
            UsageValueDisplayMode.Used);

        Assert.Equal(incidentUri, message.ActionUri);
        Assert.Equal("View incident", message.ActionLabel);
    }
}
