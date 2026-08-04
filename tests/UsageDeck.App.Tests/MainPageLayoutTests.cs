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

    private static string ReadMainPageSource()
    {
        string sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "SourceUnderTest",
            "MainPage.xaml.cs");
        return File.ReadAllText(sourcePath);
    }
}
