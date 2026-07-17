namespace CodexBarWin.App.Tests;

public sealed class BuildInformationTests
{
    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("1.2.3-beta.4", "1.2.3-beta.4")]
    [InlineData("1.2.3+abc123", "1.2.3")]
    [InlineData(" 2.0.0-rc.1+build.9 ", "2.0.0-rc.1")]
    public void NormaliseVersionKeepsSemVerVisibleWithoutBuildMetadata(
        string informationalVersion,
        string expected)
    {
        Assert.Equal(expected, BuildInformation.NormaliseVersion(informationalVersion));
    }

    [Fact]
    public void NormaliseVersionFallsBackWhenMetadataIsMissing()
    {
        Assert.Equal("0.1.0", BuildInformation.NormaliseVersion(null));
    }

    [Theory]
    [InlineData("https://github.com/example/CodexBarWin", "https://github.com/example/CodexBarWin")]
    [InlineData(" https://github.com/example/CodexBarWin/ ", "https://github.com/example/CodexBarWin")]
    public void ParseUpdateRepositoryUrlAcceptsAbsoluteHttpsUrls(string value, string expected)
    {
        Assert.Equal(expected, BuildInformation.ParseUpdateRepositoryUrl(value)?.AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("github.com/example/CodexBarWin")]
    [InlineData("http://github.com/example/CodexBarWin")]
    public void ParseUpdateRepositoryUrlRejectsMissingOrInsecureUrls(string? value)
    {
        Assert.Null(BuildInformation.ParseUpdateRepositoryUrl(value));
    }
}
