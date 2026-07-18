using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App.Tests;

public sealed class AppUpdateServiceTests
{
    [Theory]
    [InlineData(AppUpdateChannel.Stable, false)]
    [InlineData(AppUpdateChannel.Beta, true)]
    public void ShouldIncludePrereleasesMatchesUpdateChannel(
        AppUpdateChannel channel,
        bool expected)
    {
        Assert.Equal(expected, AppUpdateService.ShouldIncludePrereleases(channel));
    }

    [Fact]
    public void ShouldIncludePrereleasesRejectsUnknownUpdateChannel()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AppUpdateService.ShouldIncludePrereleases((AppUpdateChannel)int.MaxValue));
    }
}
