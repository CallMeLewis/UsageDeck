using UsageDeck.Core.Formatting;
using UsageDeck.Core.Providers;

namespace UsageDeck.App.Tests;

public sealed class RefreshAnnouncementTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SingleProviderAnnouncesItsResult()
    {
        Assert.Equal("Claude usage updated", RefreshAnnouncement.Create([Provider(ProviderId.Claude, failed: false)]));
        Assert.Equal("Claude couldn’t refresh", RefreshAnnouncement.Create([Provider(ProviderId.Claude, failed: true)]));
    }

    [Fact]
    public void SeveralProvidersAnnounceFailuresBriefly()
    {
        Assert.Equal(
            "Usage updated",
            RefreshAnnouncement.Create([Provider(ProviderId.Codex, false), Provider(ProviderId.Claude, false)]));
        Assert.Equal(
            "Usage updated. Claude couldn’t refresh",
            RefreshAnnouncement.Create([Provider(ProviderId.Codex, false), Provider(ProviderId.Claude, true)]));
        Assert.Equal(
            "Usage updated. 2 providers couldn’t refresh",
            RefreshAnnouncement.Create(
            [
                Provider(ProviderId.Codex, true),
                Provider(ProviderId.Claude, true),
                Provider(ProviderId.Amp, false),
            ]));
    }

    [Fact]
    public void TheTickingUpdatedCaptionIsNotALiveRegion()
    {
        string xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SourceUnderTest", "MainPage.xaml"));
        int caption = xaml.IndexOf("Text=\"{Binding SelectedProvider.UpdatedText}\"", StringComparison.Ordinal);
        int start = xaml.LastIndexOf("<TextBlock", caption, StringComparison.Ordinal);

        Assert.True(caption > start && start >= 0);
        Assert.DoesNotContain("LiveSetting", xaml[start..caption], StringComparison.Ordinal);
    }

    [Fact]
    public void AnnouncementsComeFromAnElementWithAnAutomationPeer()
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SourceUnderTest", "MainPage.xaml.cs"));

        // A Page has no automation peer, and announcing through it crashed the app.
        Assert.DoesNotContain("CreatePeerForElement(this)", source, StringComparison.Ordinal);
        Assert.Contains("CreatePeerForElement(this.RefreshButton)?.RaiseNotificationEvent", source, StringComparison.Ordinal);
    }

    private static ProviderTabViewModel Provider(ProviderId id, bool failed)
    {
        ProviderTabViewModel model = new(id, id.DisplayName);
        model.ApplySnapshot(
            new ProviderSnapshot(
                id,
                id.DisplayName,
                "Test source",
                Now,
                failed ? UsageDataState.Unavailable : UsageDataState.Fresh,
                safeError: failed ? "Usage could not be read." : null,
                errorCategory: failed ? ProviderErrorCategory.Unavailable : null),
            Now,
            TimeDisplayPrecision.ThirtySeconds);
        return model;
    }
}
