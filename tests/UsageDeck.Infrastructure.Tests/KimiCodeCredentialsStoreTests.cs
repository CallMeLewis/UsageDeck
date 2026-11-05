using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers.KimiCode;

namespace UsageDeck.Infrastructure.Tests;

public sealed class KimiCodeCredentialsStoreTests : IDisposable
{
    // Mirrors the config.toml Kimi Code 2.4 writes after a global sign-in.
    private const string GlobalConfig = """
        default_model = "kimi-code/kimi-for-coding"

        [providers."managed:kimi-code"]
        type = "kimi"
        api_key = ""
        base_url = "https://api.kimi.ai/coding/v1"

        [providers."managed:kimi-code".oauth]
        storage = "file"
        key = "oauth/kimi-code-env-0e4f99c69cc27850"
        oauth_host = "https://auth.kimi.ai"

        [models."kimi-code/kimi-for-coding"]
        provider = "managed:kimi-code"
        capabilities = [ "thinking", "tool_use" ]
        """;

    private readonly string _home = Path.Combine(Path.GetTempPath(), $"UsageDeck-Kimi-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(this._home))
        {
            Directory.Delete(this._home, recursive: true);
        }
    }

    [Fact]
    public void ReportsNoSignInWhenKimiCodeHasNoData()
    {
        KimiCodeCredentialsStore store = new(this._home);

        Assert.False(store.HasKimiCodeHome);
        Assert.Null(store.Read());

        Directory.CreateDirectory(this._home);
        Assert.True(store.HasKimiCodeHome);
        Assert.Null(store.Read());
    }

    [Fact]
    public void ReadsTheOAuthTokenNamedInTheConfig()
    {
        this.WriteConfig(GlobalConfig);
        this.WriteToken("kimi-code-env-0e4f99c69cc27850", """
            { "access_token": "access-token", "refresh_token": "refresh-token", "expires_at": 1793837422, "token_type": "Bearer" }
            """);
        File.WriteAllText(Path.Combine(this._home, "device_id"), "device-123\n");

        KimiCodeCredentials? credentials = new KimiCodeCredentialsStore(this._home).Read();

        Assert.NotNull(credentials);
        Assert.Equal("access-token", credentials.Token);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793837422), credentials.ExpiresAt);
        Assert.Equal(new Uri("https://api.kimi.ai/coding/v1"), credentials.BaseUri);
        Assert.Equal(
            new KimiCodeRenewal("refresh-token", new Uri("https://auth.kimi.ai"), "kimi-code-env-0e4f99c69cc27850", "device-123"),
            credentials.Renewal);
    }

    [Theory]
    [InlineData(null, "https://api.kimi.ai/coding/v1", "https://auth.kimi.ai/")]
    [InlineData(null, "https://api.kimi.com/coding/v1", "https://auth.kimi.com/")]
    [InlineData("https://auth.kimi.com/", "https://api.kimi.ai/coding/v1", "https://auth.kimi.com/")]
    [InlineData("https://auth.example.com", "https://api.kimi.ai/coding/v1", null)]
    public void RenewsOnlyThroughKimisOwnSignInService(string? configured, string baseUri, string? expected)
    {
        Assert.Equal(expected, KimiCodeCredentialsStore.ResolveOAuthHost(configured, new Uri(baseUri))?.AbsoluteUri);
    }

    [Theory]
    [InlineData("""{ "access_token": "token", "refresh_token": "", "expires_at": 1 }""")]
    [InlineData("""{ "access_token": "token", "expires_at": 1 }""")]
    public void ATokenWithoutARefreshTokenCannotBeRenewed(string token)
    {
        this.WriteConfig(GlobalConfig);
        this.WriteToken("kimi-code-env-0e4f99c69cc27850", token);

        KimiCodeCredentials? credentials = new KimiCodeCredentialsStore(this._home).Read();

        Assert.NotNull(credentials);
        Assert.Null(credentials.Renewal);
    }

    [Fact]
    public void ARevokedSignInIsTreatedAsSignedOut()
    {
        this.WriteConfig(GlobalConfig);
        this.WriteToken("kimi-code-env-0e4f99c69cc27850", """
            { "access_token": "", "refresh_token": "", "expires_at": 0, "scope": "kimi-code", "token_type": "Bearer", "expires_in": 0 }
            """);

        Assert.Null(new KimiCodeCredentialsStore(this._home).Read());
    }

    [Fact]
    public void SaveReplacesTheTokenInKimiCodesFormat()
    {
        this.WriteConfig(GlobalConfig);
        this.WriteToken("kimi-code-env-0e4f99c69cc27850", """{ "access_token": "old", "refresh_token": "old-refresh", "expires_at": 1 }""");
        KimiCodeCredentialsStore store = new(this._home);

        store.Save(
            "kimi-code-env-0e4f99c69cc27850",
            new KimiCodeTokenSet("new", "new-refresh", 1793838322, "kimi-code", "Bearer", 900));

        string directory = Path.Combine(this._home, "credentials");
        Assert.Equal(
            "{\n  \"access_token\": \"new\",\n  \"refresh_token\": \"new-refresh\",\n  \"expires_at\": 1793838322,\n"
                + "  \"scope\": \"kimi-code\",\n  \"token_type\": \"Bearer\",\n  \"expires_in\": 900\n}\n",
            File.ReadAllText(Path.Combine(directory, "kimi-code-env-0e4f99c69cc27850.json")));
        Assert.Single(Directory.GetFiles(directory));
        KimiCodeCredentials? credentials = store.Read();
        Assert.Equal("new", credentials?.Token);
        Assert.Equal("new-refresh", credentials?.Renewal?.RefreshToken);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData(".hidden")]
    [InlineData("oauth/kimi-code")]
    public void SaveRefusesNamesOutsideTheCredentialsFolder(string name)
    {
        Directory.CreateDirectory(this._home);

        Assert.Throws<ArgumentException>(() => new KimiCodeCredentialsStore(this._home).Save(
            name,
            new KimiCodeTokenSet("a", "r", 1, "", "Bearer", 1)));
    }

    [Fact]
    public void TheDefaultSignInUsesTheMainlandEndpointAndDefaultFile()
    {
        this.WriteConfig("""
            [providers."managed:kimi-code".oauth]
            key = "oauth/kimi-code"
            """);
        this.WriteToken("kimi-code", """{ "access_token": "token" }""");

        KimiCodeCredentials? credentials = new KimiCodeCredentialsStore(this._home).Read();

        Assert.NotNull(credentials);
        Assert.Null(credentials.ExpiresAt);
        Assert.Equal(new Uri("https://api.kimi.com/coding/v1"), credentials.BaseUri);
    }

    [Fact]
    public void FallsBackToAConfiguredApiKey()
    {
        this.WriteConfig("""
            [providers."managed:kimi-code"]
            api_key = "sk-kimi-key"
            base_url = "https://api.kimi.com/coding/v1/"
            """);

        KimiCodeCredentials? credentials = new KimiCodeCredentialsStore(this._home).Read();

        Assert.NotNull(credentials);
        Assert.Equal("sk-kimi-key", credentials.Token);
        Assert.Null(credentials.ExpiresAt);
    }

    [Theory]
    [InlineData("""
        [providers.other]
        api_key = "key"
        """)]
    [InlineData("""
        [providers."managed:kimi-code"]
        api_key = ""
        """)]
    [InlineData("""
        [providers."managed:kimi-code".oauth]
        key = "oauth/missing-file"
        """)]
    [InlineData("""
        [providers."managed:kimi-code".oauth]
        key = "oauth/../outside"
        """)]
    public void ReportsNoSignInWhenNoUsableTokenIsConfigured(string config)
    {
        this.WriteConfig(config);
        File.WriteAllText(Path.Combine(this._home, "outside.json"), """{ "access_token": "token" }""");

        Assert.Null(new KimiCodeCredentialsStore(this._home).Read());
    }

    [Fact]
    public void RefusesToSendTheSignInToAnUnrecognisedService()
    {
        this.WriteConfig(GlobalConfig.Replace("https://api.kimi.ai/coding/v1", "https://example.com/coding/v1", StringComparison.Ordinal));
        this.WriteToken("kimi-code-env-0e4f99c69cc27850", """{ "access_token": "token" }""");

        ProviderException exception = Assert.Throws<ProviderException>(() => new KimiCodeCredentialsStore(this._home).Read());

        Assert.Equal(ProviderErrorCategory.Unavailable, exception.Category);
        Assert.Contains("doesn't recognise", exception.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplainsThatKeyringSignInsCannotBeRead()
    {
        this.WriteConfig(GlobalConfig.Replace("storage = \"file\"", "storage = \"keyring\"", StringComparison.Ordinal));

        ProviderException exception = Assert.Throws<ProviderException>(() => new KimiCodeCredentialsStore(this._home).Read());

        Assert.Equal(ProviderErrorCategory.Unavailable, exception.Category);
        Assert.Contains("keyring", exception.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ACorruptTokenFileIsTransient()
    {
        this.WriteConfig(GlobalConfig);
        this.WriteToken("kimi-code-env-0e4f99c69cc27850", "{ \"access_token\": ");

        ProviderException exception = Assert.Throws<ProviderException>(() => new KimiCodeCredentialsStore(this._home).Read());

        Assert.Equal(ProviderErrorCategory.Transient, exception.Category);
    }

    [Theory]
    [InlineData("kimi-code", "kimi-code")]
    [InlineData("oauth/kimi-code", "kimi-code")]
    [InlineData("oauth/kimi-code-env-abc", "kimi-code-env-abc")]
    [InlineData("custom", "custom")]
    [InlineData("oauth/", null)]
    [InlineData("oauth/.hidden", null)]
    [InlineData("oauth/a/b", null)]
    [InlineData("oauth/a\\b", null)]
    [InlineData("other/name", null)]
    public void ResolvesTokenFileNamesAsKimiCodeDoes(string key, string? expected)
    {
        Assert.Equal(expected, KimiCodeCredentialsStore.ResolveTokenStorageName(key));
    }

    [Fact]
    public void ConfigParserReadsQuotedDottedKeysAndSkipsUnsupportedValues()
    {
        const string Config = """
            # comment = "ignored"
            title = "top" # trailing comment
            notes = '''
            base_url = "https://ignored.example"
            '''
            [ "providers" . "managed:kimi-code" ]
            base_url = 'C:\literal\path'
            escaped = "quote \" slash \\ tab \t unicode \u00e9"
            oauth.key = "dotted"
            count = 3
            list = [
              "item",
            ]
            [[models]]
            base_url = "array table"
            """;

        IReadOnlyDictionary<string, string> values = KimiCodeConfigParser.ParseStrings(Config);

        Assert.Equal("top", values[KimiCodeConfigParser.PathKey(["title"])]);
        Assert.Equal("C:\\literal\\path", values[KimiCodeConfigParser.PathKey(["providers", "managed:kimi-code", "base_url"])]);
        Assert.Equal("quote \" slash \\ tab \t unicode é", values[KimiCodeConfigParser.PathKey(["providers", "managed:kimi-code", "escaped"])]);
        Assert.Equal("dotted", values[KimiCodeConfigParser.PathKey(["providers", "managed:kimi-code", "oauth", "key"])]);
        Assert.Equal("array table", values[KimiCodeConfigParser.PathKey(["[]", "models", "base_url"])]);
        Assert.Equal(5, values.Count);
    }

    private void WriteConfig(string contents)
    {
        Directory.CreateDirectory(this._home);
        File.WriteAllText(Path.Combine(this._home, "config.toml"), contents);
    }

    private void WriteToken(string name, string contents)
    {
        string directory = Path.Combine(this._home, "credentials");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{name}.json"), contents);
    }
}
