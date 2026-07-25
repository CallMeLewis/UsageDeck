using System.Text.Json;

namespace UsageDeck.Infrastructure.Providers.Claude;

public sealed record ClaudeCredentials(string AccessToken, DateTimeOffset ExpiresAt);

public interface IClaudeCredentialsReader
{
    /// <summary>
    /// Reads Claude Code's OAuth access token, or returns null when the CLI has no stored
    /// credentials or they cannot be read. The token never leaves the machine except as the
    /// authorization header of a request to Anthropic's own API.
    /// </summary>
    ClaudeCredentials? Read();
}

public sealed class ClaudeCredentialsReader(string? credentialsPath = null) : IClaudeCredentialsReader
{
    private readonly string _credentialsPath = credentialsPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        ".credentials.json");

    public ClaudeCredentials? Read()
    {
        try
        {
            if (!File.Exists(this._credentialsPath))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(this._credentialsPath));
            if (!document.RootElement.TryGetProperty("claudeAiOauth", out JsonElement oauth)
                || oauth.ValueKind != JsonValueKind.Object
                || !oauth.TryGetProperty("accessToken", out JsonElement token)
                || token.ValueKind != JsonValueKind.String
                || !oauth.TryGetProperty("expiresAt", out JsonElement expiresAt)
                || expiresAt.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            string? accessToken = token.GetString();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return null;
            }

            return new ClaudeCredentials(
                accessToken,
                DateTimeOffset.FromUnixTimeMilliseconds(expiresAt.GetInt64()));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }
}
