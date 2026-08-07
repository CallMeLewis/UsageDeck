using System.Xml.Linq;

namespace UsageDeck.App.Tests;

public sealed class MainPageLayoutTests
{
    [Fact]
    public void InitialContentTransitionDoesNotForceSynchronousLayout()
    {
        string source = ReadMainPageSource();
        int methodStart = source.IndexOf(
            "private void ShowInitialContent()",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        int methodEnd = source.IndexOf(
            "private void StartSkeletonShimmer()",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);

        Assert.DoesNotContain(
            ".UpdateLayout()",
            source[methodStart..methodEnd],
            StringComparison.Ordinal);
    }

    [Fact]
    public void TrayCommandsAreInitialisedBeforeTheWindowIsActivated()
    {
        string sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "SourceUnderTest",
            "MainWindow.xaml.cs");
        string source = File.ReadAllText(sourcePath);

        int initialiseComponent = source.IndexOf(
            "this.InitializeComponent();",
            StringComparison.Ordinal);
        int updateBindings = source.IndexOf(
            "this.Bindings.Update();",
            initialiseComponent,
            StringComparison.Ordinal);
        int readApplication = source.IndexOf(
            "App app = (App)Application.Current;",
            initialiseComponent,
            StringComparison.Ordinal);

        Assert.True(initialiseComponent >= 0);
        Assert.True(updateBindings > initialiseComponent);
        Assert.True(readApplication > updateBindings);
    }

    [Fact]
    public void MultiProviderRefreshKeepsUiWorkOutOfTheBackgroundBatch()
    {
        string source = ReadMainPageSource();
        int methodStart = source.IndexOf(
            "private async Task RefreshProvidersAsync(",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        int methodEnd = source.IndexOf(
            "private async Task RefreshProviderAsync(",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);

        string method = source[methodStart..methodEnd];
        Assert.Contains(
            ".Select(this.RefreshProviderAsync)",
            method,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ProviderRefreshBatch.RunAsync",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsNavigationOmitsUnavailableOpenCodeGo()
    {
        string sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "SourceUnderTest",
            "SettingsWindow.xaml");
        string source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("Tag=\"provider:opencode-go\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPagesConstrainContentToTheVisibleWidth()
    {
        string sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "SourceUnderTest",
            "SettingsWindow.xaml");
        XDocument xaml = XDocument.Load(sourcePath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] pageNames =
        [
            "GeneralPanel",
            "AppearancePanel",
            "NotificationsPanel",
            "ProviderPanel",
            "DebugPanel",
            "AboutPanel",
        ];

        foreach (string pageName in pageNames)
        {
            XElement page = Assert.Single(
                xaml.Descendants(presentation + "ScrollViewer"),
                element => string.Equals(
                    (string?)element.Attribute(x + "Name"),
                    pageName,
                    StringComparison.Ordinal));
            Assert.Equal("Disabled", (string?)page.Attribute("HorizontalScrollMode"));
            Assert.Equal("Disabled", (string?)page.Attribute("HorizontalScrollBarVisibility"));
        }
    }

    [Fact]
    public void MainPageRequestsInstallationOnlyOnTheNextClickAfterDownload()
    {
        string method = ReadMethod(
            ReadMainPageSource(),
            "private async void UpdateActionButton_Click",
            "private void MainPage_ActualThemeChanged");

        int download = method.IndexOf("await updater.DownloadUpdateAsync", StringComparison.Ordinal);
        int confirmation = method.IndexOf(
            "await UpdateInstallationDialog.ShowAsync",
            StringComparison.Ordinal);
        int restart = method.IndexOf("app.RestartForUpdate()", StringComparison.Ordinal);
        int returnAfterDownload = method.IndexOf("return;", download, StringComparison.Ordinal);

        Assert.True(download >= 0);
        Assert.True(returnAfterDownload > download);
        Assert.True(confirmation > returnAfterDownload);
        Assert.True(restart > confirmation);
    }

    [Fact]
    public void SettingsRequestsInstallationOnlyOnTheNextClickAfterDownload()
    {
        string sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "SourceUnderTest",
            "SettingsWindow.xaml.cs");
        string method = ReadMethod(
            File.ReadAllText(sourcePath),
            "private async void UpdateActionButton_Click",
            "private void App_UpdateStateChanged");

        int download = method.IndexOf("await updater.DownloadUpdateAsync", StringComparison.Ordinal);
        int confirmation = method.IndexOf(
            "await UpdateInstallationDialog.ShowAsync",
            StringComparison.Ordinal);
        int restart = method.IndexOf("app.RestartForUpdate()", StringComparison.Ordinal);
        int returnAfterConfirmation = method.IndexOf("return;", restart, StringComparison.Ordinal);

        Assert.True(download >= 0);
        Assert.True(confirmation >= 0);
        Assert.True(restart > confirmation);
        Assert.True(returnAfterConfirmation > restart);
        Assert.True(download > returnAfterConfirmation);
    }

    private static string ReadMainPageSource()
    {
        string sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "SourceUnderTest",
            "MainPage.xaml.cs");
        return File.ReadAllText(sourcePath);
    }

    private static string ReadMethod(string source, string startMarker, string endMarker)
    {
        int methodStart = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        int methodEnd = source.IndexOf(endMarker, methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        return source[methodStart..methodEnd];
    }
}
