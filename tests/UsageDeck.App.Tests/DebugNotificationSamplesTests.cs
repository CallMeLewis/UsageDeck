#if DEBUG
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App.Tests;

public sealed class DebugNotificationSamplesTests
{
    [Fact]
    public void EveryScenarioProducesACompleteNotificationMessage()
    {
        foreach (DebugNotificationScenario scenario in Enum.GetValues<DebugNotificationScenario>())
        {
            NotificationMessage message = NotificationMessageFormatter.Format(
                DebugNotificationSamples.Create(scenario),
                UsageValueDisplayMode.Remaining);

            Assert.False(string.IsNullOrWhiteSpace(message.Title));
            Assert.False(string.IsNullOrWhiteSpace(message.Body));
            Assert.Equal("codex", message.ProviderId.Value);
        }
    }

    [Fact]
    public void IncidentScenarioExercisesTheNotificationAction()
    {
        NotificationMessage message = NotificationMessageFormatter.Format(
            DebugNotificationSamples.Create(DebugNotificationScenario.IncidentDetected),
            UsageValueDisplayMode.Used);

        Assert.Equal("View incident", message.ActionLabel);
        Assert.Equal(new Uri("https://status.openai.com/"), message.ActionUri);
    }
}
#endif
