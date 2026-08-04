namespace UsageDeck.App.Tests;

public sealed class ReleaseNotesTests
{
    [Fact]
    public void ParseBuildsStructuredNotesAndIgnoresOptionalTitle()
    {
        const string Markdown = """
            # UsageDeck 1.2.3

            A concise introduction to the release.

            ## Added

            - A **clearer** first change.
            - A second change using `local data`.

            ## Fixes

            - A corrected behaviour.

            Restart UsageDeck after installing this version.
            """;

        ReleaseNotesDocument document = ReleaseNotesReader.Parse("1.2.3", Markdown);

        Assert.Equal("1.2.3", document.Version);
        Assert.Equal(["A concise introduction to the release."], document.Introduction);
        Assert.Collection(
            document.Sections,
            added =>
            {
                Assert.Equal("Added", added.Heading);
                Assert.Equal(
                    ["A **clearer** first change.", "A second change using `local data`."],
                    added.Items);
            },
            fixes =>
            {
                Assert.Equal("Fixes", fixes.Heading);
                Assert.Equal(["A corrected behaviour."], fixes.Items);
            });
        Assert.Equal(
            ["Restart UsageDeck after installing this version."],
            document.ClosingNotes);
    }

    [Fact]
    public void ParseSupportsCurrentReleaseNoteFormatWithoutTitle()
    {
        const string Markdown = """
            This beta improves the installed experience.

            ## Improvements

            - The first improvement.
            """;

        ReleaseNotesDocument document = ReleaseNotesReader.Parse("2.0.0-beta.1", Markdown);

        Assert.Equal(["This beta improves the installed experience."], document.Introduction);
        ReleaseNotesSection section = Assert.Single(document.Sections);
        Assert.Equal("Improvements", section.Heading);
        Assert.Equal(["The first improvement."], section.Items);
        Assert.Empty(document.ClosingNotes);
    }

    [Fact]
    public void LoadReportsWhenNotesAreNotBundled()
    {
        string missingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"UsageDeck-missing-release-notes-{Guid.NewGuid():N}");

        ReleaseNotesLoadResult result = ReleaseNotesReader.Load(missingDirectory, "1.0.0");

        Assert.False(result.IsAvailable);
        Assert.Null(result.Document);
        Assert.Equal("Release notes are not included with this build.", result.UnavailableMessage);
    }
}
