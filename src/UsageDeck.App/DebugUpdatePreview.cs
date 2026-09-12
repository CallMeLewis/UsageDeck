#if DEBUG
namespace UsageDeck.App;

internal sealed class DebugUpdatePreview
{
    private static readonly AppUpdateAvailability PreviewUpdate = new(
        "0.0.0-preview",
        "## Icon preview\n\n- This is a simulated update for checking the footer icons.\n- Download shows sample progress. Install resets the preview without installing anything.");

    public AppUpdateAvailability? AvailableUpdate { get; private set; }

    public bool IsDownloaded { get; private set; }

    public void Advance()
    {
        if (this.AvailableUpdate is null)
        {
            this.AvailableUpdate = PreviewUpdate;
        }
        else if (!this.IsDownloaded)
        {
            this.IsDownloaded = true;
        }
        else
        {
            this.AvailableUpdate = null;
            this.IsDownloaded = false;
        }
    }
}
#endif
