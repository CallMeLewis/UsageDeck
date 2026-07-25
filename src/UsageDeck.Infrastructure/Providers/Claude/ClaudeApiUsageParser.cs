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
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using JsonDocument document = ParseDocument(json);
        if (!document.RootElement.TryGetProperty("limits", out JsonElement limits)
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
        DateTimeOffset? resetsAt = ReadResetsAt(limit);

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

    private static DateTimeOffset? ReadResetsAt(JsonElement limit)
    {
        if (!limit.TryGetProperty("resets_at", out JsonElement resetsAt)
            || resetsAt.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                resetsAt.GetString(),
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
