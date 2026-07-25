using UsageDeck.Infrastructure.Providers.Claude;

namespace UsageDeck.Infrastructure.Tests;

public sealed class ClaudeCredentialsReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"usagedeck-claude-credentials-{Guid.NewGuid():N}.json");

    [Fact]
    public void ReadReturnsTokenAndExpiry()
    {
        File.WriteAllText(this._path, """
            {
              "claudeAiOauth": {
                "accessToken": "token-value",
                "refreshToken": "refresh-value",
                "expiresAt": 1785025642000,
                "scopes": ["user:inference"],
                "subscriptionType": "max"
              }
            }
            """);

        ClaudeCredentials? credentials = new ClaudeCredentialsReader(this._path).Read();

        Assert.NotNull(credentials);
        Assert.Equal("token-value", credentials.AccessToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1785025642000), credentials.ExpiresAt);
    }

    [Fact]
    public void ReadReturnsNullWhenTheFileIsMissing()
    {
        Assert.Null(new ClaudeCredentialsReader(this._path).Read());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "claudeAiOauth": { "accessToken": "", "expiresAt": 1785025642000 } }""")]
    [InlineData("""{ "claudeAiOauth": { "accessToken": "token" } }""")]
    public void ReadReturnsNullWhenTheFileIsUnusable(string content)
    {
        File.WriteAllText(this._path, content);

        Assert.Null(new ClaudeCredentialsReader(this._path).Read());
    }

    public void Dispose()
    {
        if (File.Exists(this._path))
        {
            File.Delete(this._path);
        }
    }
}
