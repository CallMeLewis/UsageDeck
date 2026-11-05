using System.Security.Cryptography;
using System.Text.Json;
using UsageDeck.Core.Providers;

namespace UsageDeck.Infrastructure.Providers.KimiCode;

/// <param name="Token">The OAuth access token, or an API key when Kimi Code is set up with one.</param>
/// <param name="ExpiresAt">When the access token lapses, or null for an API key.</param>
/// <param name="BaseUri">Kimi Code's managed API, always one of <see cref="KimiCodeCredentialsStore.KnownBaseUris"/>.</param>
public sealed record KimiCodeCredentials(string Token, DateTimeOffset? ExpiresAt, Uri BaseUri)
{
    /// <summary>How to renew the access token, or null when it cannot be renewed.</summary>
    public KimiCodeRenewal? Renewal { get; init; }
}

/// <param name="OAuthHost">Kimi Code's sign-in service, always one of <see cref="KimiCodeCredentialsStore.KnownOAuthHosts"/>.</param>
/// <param name="StorageName">The credentials file Kimi Code keeps the token in, without its extension.</param>
/// <param name="DeviceId">The device identifier Kimi Code signed in with, when it recorded one.</param>
public sealed record KimiCodeRenewal(string RefreshToken, Uri OAuthHost, string StorageName, string? DeviceId);

/// <summary>A renewed token, in the shape of the credentials file Kimi Code writes.</summary>
public sealed record KimiCodeTokenSet(
    string AccessToken,
    string RefreshToken,
    long ExpiresAt,
    string Scope,
    string TokenType,
    long ExpiresIn);

public interface IKimiCodeCredentialsStore
{
    /// <summary>True when Kimi Code has created its data folder on this PC.</summary>
    bool HasKimiCodeHome { get; }

    /// <summary>
    /// Reads the sign-in Kimi Code stored for its managed provider, or returns null when it is
    /// not signed in.
    /// </summary>
    /// <exception cref="ProviderException">The sign-in exists but cannot be used safely.</exception>
    KimiCodeCredentials? Read();

    /// <summary>
    /// Replaces Kimi Code's stored token with a renewed one, atomically and in Kimi Code's own
    /// format, so a running Kimi Code picks it up on its next request.
    /// </summary>
    void Save(string storageName, KimiCodeTokenSet tokens);
}

public sealed class KimiCodeCredentialsStore : IKimiCodeCredentialsStore
{
    private const string ManagedProviderName = "managed:kimi-code";
    private const string DefaultOAuthKey = "oauth/kimi-code";
    private const int MaximumFileBytes = 1_048_576;
    private const int MaximumReplaceAttempts = 20;

    private static readonly JsonWriterOptions TokenFileWriterOptions = new() { Indented = true, NewLine = "\n" };

    private readonly string _homeDirectory;

    public KimiCodeCredentialsStore(string? homeDirectory = null)
    {
        this._homeDirectory = homeDirectory ?? DefaultHomeDirectory();
    }

    /// <summary>
    /// The mainland China and global managed endpoints Kimi Code ships with. The access token is
    /// only ever sent to one of these, whatever config.toml says.
    /// </summary>
    public static IReadOnlyList<Uri> KnownBaseUris { get; } =
    [
        new("https://api.kimi.com/coding/v1"),
        new("https://api.kimi.ai/coding/v1"),
    ];

    /// <summary>The sign-in services matching <see cref="KnownBaseUris"/>, in the same order.</summary>
    public static IReadOnlyList<Uri> KnownOAuthHosts { get; } =
    [
        new("https://auth.kimi.com"),
        new("https://auth.kimi.ai"),
    ];

    public bool HasKimiCodeHome => Directory.Exists(this._homeDirectory);

    public KimiCodeCredentials? Read()
    {
        try
        {
            string configPath = Path.Combine(this._homeDirectory, "config.toml");
            if (!File.Exists(configPath))
            {
                return null;
            }

            IReadOnlyDictionary<string, string> config = KimiCodeConfigParser.ParseStrings(ReadBoundedText(configPath));
            string[] provider = ["providers", ManagedProviderName];
            string[] oauth = [.. provider, "oauth"];
            bool hasProvider = config.Keys.Any(key => key.StartsWith(
                KimiCodeConfigParser.PathKey(provider) + KimiCodeConfigParser.PathSeparator,
                StringComparison.Ordinal));
            if (!hasProvider)
            {
                return null;
            }

            Uri baseUri = ResolveBaseUri(config.GetValueOrDefault(KimiCodeConfigParser.PathKey([.. provider, "base_url"])));
            string? oauthKey = config.GetValueOrDefault(KimiCodeConfigParser.PathKey([.. oauth, "key"]));
            if (!string.IsNullOrWhiteSpace(oauthKey))
            {
                string storage = config.GetValueOrDefault(KimiCodeConfigParser.PathKey([.. oauth, "storage"])) ?? "file";
                if (!storage.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ProviderException(
                        ProviderErrorCategory.Unavailable,
                        "Kimi Code keeps its sign-in in the system keyring, which UsageDeck doesn't read.");
                }

                Uri? oauthHost = ResolveOAuthHost(
                    config.GetValueOrDefault(KimiCodeConfigParser.PathKey([.. oauth, "oauth_host"])),
                    baseUri);
                return this.ReadOAuthToken(oauthKey.Trim(), baseUri, oauthHost);
            }

            string? apiKey = config.GetValueOrDefault(KimiCodeConfigParser.PathKey([.. provider, "api_key"]))?.Trim();
            return string.IsNullOrEmpty(apiKey) ? null : new KimiCodeCredentials(apiKey, null, baseUri);
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ProviderException(
                ProviderErrorCategory.Transient,
                "Kimi Code's sign-in could not be read.",
                exception);
        }
    }

    public void Save(string storageName, KimiCodeTokenSet tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (ResolveTokenStorageName(storageName) != storageName)
        {
            throw new ArgumentException("The Kimi Code token name is not a plain file name.", nameof(storageName));
        }

        string directory = Path.Combine(this._homeDirectory, "credentials");
        string target = Path.Combine(directory, $"{storageName}.json");
        string temporary = $"{target}.tmp.{Environment.ProcessId}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                using (Utf8JsonWriter writer = new(stream, TokenFileWriterOptions))
                {
                    writer.WriteStartObject();
                    writer.WriteString("access_token", tokens.AccessToken);
                    writer.WriteString("refresh_token", tokens.RefreshToken);
                    writer.WriteNumber("expires_at", tokens.ExpiresAt);
                    writer.WriteString("scope", tokens.Scope);
                    writer.WriteString("token_type", tokens.TokenType);
                    writer.WriteNumber("expires_in", tokens.ExpiresIn);
                    writer.WriteEndObject();
                }

                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }

            ReplaceWithRetries(temporary, target);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Maps an OAuth key from config.toml to its file name, as Kimi Code does.</summary>
    internal static string? ResolveTokenStorageName(string oauthKey)
    {
        string name = oauthKey is "kimi-code" or DefaultOAuthKey
            ? "kimi-code"
            : oauthKey.StartsWith("oauth/", StringComparison.Ordinal)
                ? oauthKey["oauth/".Length..]
                : oauthKey;
        bool isPlainFileName = name.Length > 0
            && !name.StartsWith('.')
            && name.IndexOfAny(['/', '\\']) < 0
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        return isPlainFileName ? name : null;
    }

    internal static Uri ResolveBaseUri(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return KnownBaseUris[0];
        }

        return FindKnown(KnownBaseUris, configured) ?? throw new ProviderException(
            ProviderErrorCategory.Unavailable,
            "Kimi Code is set up to use a service address UsageDeck doesn't recognise, so UsageDeck won't send your sign-in there.");
    }

    /// <summary>
    /// Returns the sign-in service to renew with, or null when the configured one is unknown, in
    /// which case the token is used until it lapses but never sent anywhere to be renewed.
    /// </summary>
    internal static Uri? ResolveOAuthHost(string? configured, Uri baseUri) =>
        string.IsNullOrWhiteSpace(configured)
            ? KnownOAuthHosts[KnownBaseUris.ToList().IndexOf(baseUri)]
            : FindKnown(KnownOAuthHosts, configured);

    private static Uri? FindKnown(IReadOnlyList<Uri> known, string configured)
    {
        string normalised = configured.Trim().TrimEnd('/');
        return known.FirstOrDefault(uri =>
            string.Equals(uri.AbsoluteUri.TrimEnd('/'), normalised, StringComparison.OrdinalIgnoreCase));
    }

    private static void ReplaceWithRetries(string source, string target)
    {
        // Kimi Code may be reading the file at the same moment; wait briefly rather than lose
        // a renewed token, because the refresh token it replaces no longer works.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                attempt < MaximumReplaceAttempts && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25);
            }
        }
    }

    private KimiCodeCredentials? ReadOAuthToken(string oauthKey, Uri baseUri, Uri? oauthHost)
    {
        string? name = ResolveTokenStorageName(oauthKey);
        if (name is null)
        {
            return null;
        }

        string tokenPath = Path.Combine(this._homeDirectory, "credentials", $"{name}.json");
        if (!File.Exists(tokenPath))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(ReadBoundedText(tokenPath));
        JsonElement root = document.RootElement;
        string? accessToken = GetString(root, "access_token");
        if (string.IsNullOrEmpty(accessToken))
        {
            // Kimi Code leaves an empty token behind when its sign-in has been revoked.
            return null;
        }

        DateTimeOffset? expiresAt = null;
        if (root.TryGetProperty("expires_at", out JsonElement expiry)
            && expiry.ValueKind == JsonValueKind.Number
            && expiry.TryGetDouble(out double seconds)
            && seconds is > 0 and < 253402300799)
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds((long)seconds);
        }

        string? refreshToken = GetString(root, "refresh_token");
        return new KimiCodeCredentials(accessToken, expiresAt, baseUri)
        {
            Renewal = string.IsNullOrEmpty(refreshToken) || oauthHost is null
                ? null
                : new KimiCodeRenewal(refreshToken, oauthHost, name, this.ReadDeviceId()),
        };
    }

    private string? ReadDeviceId()
    {
        try
        {
            string path = Path.Combine(this._homeDirectory, "device_id");
            if (!File.Exists(path))
            {
                return null;
            }

            string value = ReadBoundedText(path).Trim();
            return value.Length is > 0 and <= 128 && value.All(character => character is > ' ' and <= '~')
                ? value
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null;

    private static string ReadBoundedText(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaximumFileBytes)
        {
            throw new IOException("The Kimi Code file is larger than expected.");
        }

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static string DefaultHomeDirectory()
    {
        string? overridePath = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        return !string.IsNullOrWhiteSpace(overridePath)
            ? overridePath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code");
    }
}
