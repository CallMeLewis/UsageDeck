using System.Text.Json;
using System.Text.RegularExpressions;

namespace UsageDeck.Infrastructure.Providers.Claude;

/// <param name="Plan">
/// The subscription tier Claude Code recorded at sign-in, such as "ultra", or null when the
/// CLI did not record one.
/// </param>
public sealed record ClaudeCredentials(string AccessToken, DateTimeOffset ExpiresAt, string? Plan = null);

public interface IClaudeCredentialsReader
{
    /// <summary>
    /// Reads Claude Code's OAuth access token, or returns null when the CLI has no stored
    /// credentials or they cannot be read. The token never leaves the machine except as the
    /// authorization header of a request to Anthropic's own API.
    /// </summary>
    ClaudeCredentials? Read();
}

public sealed partial class ClaudeCredentialsReader(string? credentialsPath = null) : IClaudeCredentialsReader
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
                DateTimeOffset.FromUnixTimeMilliseconds(expiresAt.GetInt64()),
                DescribePlan(GetString(oauth, "subscriptionType"), GetString(oauth, "rateLimitTier")));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Names the plan from the subscription type and the rate limit tier. Anthropic renamed the
    /// two Max tiers to Max and Ultra but Claude Code still records them as "max" with
    /// "default_claude_max_5x" or "default_claude_max_20x", so those map to the current names.
    /// Any other tier with a usage multiplier keeps it, so "team" with a 5x tier becomes "team 5x".
    /// </summary>
    internal static string? DescribePlan(string? subscriptionType, string? rateLimitTier)
    {
        if (string.IsNullOrWhiteSpace(subscriptionType))
        {
            return null;
        }

        string type = subscriptionType.Trim();
        Match multiplier = TierMultiplierRegex().Match(rateLimitTier ?? string.Empty);
        if (!multiplier.Success)
        {
            return type;
        }

        string usage = multiplier.Groups["multiplier"].Value.ToLowerInvariant();
        if (type.Equals("max", StringComparison.OrdinalIgnoreCase))
        {
            switch (usage)
            {
                case "20x":
                    return "ultra";
                case "5x":
                    return "max";
            }
        }

        return $"{type} {usage}";
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex("_(?<multiplier>\\d+x)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TierMultiplierRegex();
}
