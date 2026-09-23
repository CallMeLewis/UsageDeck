using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using UsageDeck.Core.Providers;

namespace UsageDeck.Infrastructure.Providers.Claude;

/// <summary>
/// Maps the response of Claude Code's OAuth usage endpoint to usage windows. The endpoint is
/// what the CLI's own /usage panel is drawn from, so the ids and display names here mirror
/// what <see cref="ClaudeUsageParser"/> produces from a captured panel.
/// </summary>
public static partial class ClaudeApiUsageParser
{
    public static IReadOnlyList<UsageWindow> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using JsonDocument document = ParseDocument(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("limits", out JsonElement limits)
            || limits.ValueKind != JsonValueKind.Array)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Claude's usage API did not include any usage limits.");
        }

        List<UsageWindow> windows = [];
        foreach (JsonElement limit in limits.EnumerateArray())
        {
            UsageWindow? window = MapLimit(limit);
            if (window is not null)
            {
                windows.Add(window);
            }
        }

        if (!windows.Any(window => window.Id == "session"))
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Claude's usage API did not include a session limit.");
        }

        return windows;
    }

    /// <summary>
    /// Reads the limit resets Claude Code offers through its /limit-reset command. The endpoint
    /// only fills in the <c>cedar_ember</c> block when asked for it, and reports the account as
    /// ineligible unless the request identifies as a current Claude Code CLI. Returns null when
    /// the block is missing, malformed, or the account is not eligible, so the resets section
    /// stays hidden rather than claiming there are none.
    /// </summary>
    public static RateLimitResetCredits? ParseResetCredits(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using JsonDocument document = ParseDocument(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("cedar_ember", out JsonElement status)
            || status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("eligible", out JsonElement eligible)
            || eligible.ValueKind != JsonValueKind.True
            || !status.TryGetProperty("grants", out JsonElement grants)
            || grants.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // A grant can hold several resets that share one deadline, so each remaining reset is
        // listed on its own to match how Codex reports them.
        List<RateLimitResetCredit> credits = [];
        foreach (JsonElement grant in grants.EnumerateArray())
        {
            if (grant.ValueKind != JsonValueKind.Object
                || !grant.TryGetProperty("resets_left", out JsonElement resetsLeft)
                || !resetsLeft.TryGetInt32(out int count)
                || count <= 0)
            {
                continue;
            }

            DateTimeOffset? endsAt = ReadTimestamp(grant, "ends_at");
            credits.AddRange(Enumerable.Repeat(new RateLimitResetCredit(endsAt), count));
        }

        return new RateLimitResetCredits(credits.Count, credits);
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Claude's usage API returned a response that could not be read.",
                exception);
        }
    }

    private static UsageWindow? MapLimit(JsonElement limit)
    {
        if (limit.ValueKind != JsonValueKind.Object
            || !limit.TryGetProperty("kind", out JsonElement kindElement)
            || kindElement.ValueKind != JsonValueKind.String
            || !limit.TryGetProperty("percent", out JsonElement percentElement)
            || percentElement.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        string kind = kindElement.GetString()!;
        double percent = Math.Clamp(percentElement.GetDouble(), 0, 100);
        DateTimeOffset? resetsAt = ReadTimestamp(limit, "resets_at");

        (string Id, string DisplayName)? identity = kind switch
        {
            "session" => ("session", "Current session"),
            "weekly_all" => ("weekly", "Weekly limit"),
            "weekly_scoped" => ScopedIdentity(limit),
            _ => null,
        };

        if (identity is null)
        {
            return null;
        }

        return new UsageWindow(
            identity.Value.Id,
            identity.Value.DisplayName,
            percent,
            resetsAt,
            confidence: UsageConfidence.Authoritative);
    }

    private static (string Id, string DisplayName) ScopedIdentity(JsonElement limit)
    {
        string? model = null;
        if (limit.TryGetProperty("scope", out JsonElement scope)
            && scope.ValueKind == JsonValueKind.Object
            && scope.TryGetProperty("model", out JsonElement modelElement)
            && modelElement.ValueKind == JsonValueKind.Object
            && modelElement.TryGetProperty("display_name", out JsonElement displayName)
            && displayName.ValueKind == JsonValueKind.String)
        {
            model = displayName.GetString()?.Trim();
        }

        if (string.IsNullOrEmpty(model))
        {
            return ("weekly-model", "Weekly model limit");
        }

        string slug = NonAlphaNumericRegex().Replace(model.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrEmpty(slug)
            ? ("weekly-model", "Weekly model limit")
            : ($"weekly-{slug}", $"{model} weekly");
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement container, string propertyName)
    {
        if (!container.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed))
        {
            return null;
        }

        return parsed;
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex NonAlphaNumericRegex();
}
