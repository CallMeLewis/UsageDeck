namespace UsageDeck.App.Tests;

public sealed class MainPageLayoutTests
{
    [Fact]
    public void InitialContentTransitionDoesNotForceSynchronousLayout()
    {
        string sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "SourceUnderTest",
            "MainPage.xaml.cs");
        string source = File.ReadAllText(sourcePath);
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
}
