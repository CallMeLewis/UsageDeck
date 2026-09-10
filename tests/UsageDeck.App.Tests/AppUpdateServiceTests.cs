using UsageDeck.Infrastructure.Settings;
using Velopack;

namespace UsageDeck.App.Tests;

public sealed class AppUpdateServiceTests
{
    [Theory]
    [InlineData("1.1.0", "1.1.0", AppUpdateChannel.Stable, true)]
    [InlineData("1.2.0", "1.1.0", AppUpdateChannel.Stable, false)]
    [InlineData("1.1.0-beta.1", "1.1.0-beta.1", AppUpdateChannel.Stable, false)]
    [InlineData("1.1.0-beta.1", "1.1.0-beta.1", AppUpdateChannel.Beta, true)]
    [InlineData("1.1.0", "1.2.0-beta.1", AppUpdateChannel.Stable, false)]
    public void DownloadedPackageMustMatchTheOfferedVersionAndChannel(
        string availableVersion, string downloadedVersion, AppUpdateChannel channel, bool matches)
    {
        VelopackAsset available = Asset(availableVersion);
        VelopackAsset downloaded = Asset(downloadedVersion);

        VelopackAsset? result = AppUpdateService.GetMatchingDownloadedUpdate(available, downloaded, channel);

        Assert.Equal(matches, result is not null);
        if (matches)
        {
            Assert.Same(downloaded, result);
        }
    }

    [Fact]
    public void PendingPackageCannotBeInstalledWithoutAnAvailableRelease()
    {
        Assert.Null(AppUpdateService.GetMatchingDownloadedUpdate(null, Asset("1.1.0"), AppUpdateChannel.Stable));
        Assert.Null(AppUpdateService.GetMatchingDownloadedUpdate(Asset("1.1.0"), null, AppUpdateChannel.Stable));
    }

    [Fact]
    public void DownloadedPackageMustMatchTheOfferedPackageIdentity()
    {
        VelopackAsset available = Asset("1.1.0");
        VelopackAsset downloaded = Asset("1.1.0");
        downloaded.PackageId = "AnotherApp";
        Assert.Null(AppUpdateService.GetMatchingDownloadedUpdate(available, downloaded, AppUpdateChannel.Stable));
        downloaded = Asset("1.1.0");
        downloaded.FileName = "different-package.nupkg";
        Assert.Null(AppUpdateService.GetMatchingDownloadedUpdate(available, downloaded, AppUpdateChannel.Stable));
    }

    [Fact]
    public void AvailableUpdateCarriesTheTargetReleasesNotes()
    {
        VelopackAsset target = Asset("2.0.0-beta.2");
        target.NotesMarkdown = "## Added\n\n- A new feature.";

        AppUpdateAvailability available = AppUpdateAvailability.FromAsset(target);
        ReleaseNotesLoadResult notes = ReleaseNotesReader.FromUpdate(available.Version, available.NotesMarkdown);

        Assert.Equal("2.0.0-beta.2", available.Version);
        Assert.Equal(target.NotesMarkdown, available.NotesMarkdown);
        Assert.NotNull(notes.Document);
        Assert.Equal(available.Version, notes.Document.Version);
        Assert.Equal("A new feature.", Assert.Single(Assert.Single(notes.Document.Sections).Items));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# Release title\n\n## Fixes")]
    public void MissingUpdateNotesRemainExplicit(string? markdown)
    {
        ReleaseNotesLoadResult notes = ReleaseNotesReader.FromUpdate("2.0.0", markdown);

        Assert.False(notes.IsAvailable);
        Assert.Null(notes.Document);
        Assert.Equal("Release notes are not available for this update.", notes.UnavailableMessage);
    }

    private static VelopackAsset Asset(string version) => new()
    {
        PackageId = "UsageDeck",
        Version = SemanticVersion.Parse(version),
        Type = VelopackAssetType.Full,
        FileName = $"UsageDeck-{version}-full.nupkg",
    };

    [Fact]
    public void AutomaticUpdateChecksRunEverySixHours()
    {
        Assert.Equal(TimeSpan.FromHours(6), App.AutomaticUpdateCheckInterval);
    }

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
