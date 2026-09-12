#if DEBUG
namespace UsageDeck.App.Tests;

public sealed class DebugUpdatePreviewTests
{
    [Fact]
    public void PreviewCyclesThroughAvailableDownloadedAndNoUpdateWithoutRealUpdater()
    {
        DebugUpdatePreview preview = new();
        Assert.Null(preview.AvailableUpdate);
        Assert.False(preview.IsDownloaded);

        preview.Advance();
        Assert.Equal("0.0.0-preview", preview.AvailableUpdate?.Version);
        Assert.Contains("simulated update", preview.AvailableUpdate!.NotesMarkdown, StringComparison.Ordinal);
        Assert.False(preview.IsDownloaded);

        preview.Advance();
        Assert.NotNull(preview.AvailableUpdate);
        Assert.True(preview.IsDownloaded);

        preview.Advance();
        Assert.Null(preview.AvailableUpdate);
        Assert.False(preview.IsDownloaded);

        preview.Advance();
        Assert.NotNull(preview.AvailableUpdate);
        Assert.False(preview.IsDownloaded);
    }
}
#endif
