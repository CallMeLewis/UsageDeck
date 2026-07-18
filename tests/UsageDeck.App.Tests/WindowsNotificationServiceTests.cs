using Microsoft.Windows.AppNotifications;

namespace UsageDeck.App.Tests;

public sealed class WindowsNotificationServiceTests
{
    [Fact]
    public void UnsupportedBuildDoesNotAcquireTheNotificationManager()
    {
        int managerFactoryCalls = 0;
        using WindowsNotificationService service = new(
            supportProbe: () => false,
            managerFactory: () =>
            {
                managerFactoryCalls++;
                throw new InvalidOperationException("The manager must not be acquired when unsupported.");
            });

        bool initialised = service.Initialise();

        Assert.False(initialised);
        Assert.Equal(0, managerFactoryCalls);
    }

    [Fact]
    public void RegistrationUsesTheUsageDeckIdentity()
    {
        Uri iconUri = WindowsNotificationService.CreateIconUri();

        Assert.Equal("UsageDeck", WindowsNotificationService.DisplayName);
        Assert.True(iconUri.IsFile);
        Assert.EndsWith("Assets/AppIcon.png", iconUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AppNotificationSetting.DisabledForApplication, "UsageDeck")]
    [InlineData(AppNotificationSetting.DisabledForUser, "user account")]
    [InlineData(AppNotificationSetting.DisabledByGroupPolicy, "group policy")]
    [InlineData(AppNotificationSetting.DisabledByManifest, "manifest")]
    [InlineData(AppNotificationSetting.Unsupported, "does not support")]
    public void DisabledSettingProducesAnActionableMessage(
        AppNotificationSetting setting,
        string expectedText)
    {
        string message = WindowsNotificationService.DescribeSetting(setting);

        Assert.Contains(expectedText, message, StringComparison.OrdinalIgnoreCase);
    }
}
