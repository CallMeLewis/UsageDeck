using System.Globalization;
using System.Text.Json;
using UsageDeck.Core.Providers;

namespace UsageDeck.Infrastructure.Providers.KimiCode;

public static class KimiCodeUsageParser
{
    public const string SourceDescription = "Kimi Code usage service";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    // The windows Kimi Code's own /usage panel lists, in the same order. The monthly code-only
    // share is part of the monthly total rather than a separate limit, so it is not shown.
    private static readonly (string Property, string Id, string DisplayName, TimeSpan? Duration)[] Windows =
    [
        ("limit_5h", "5-hour", "5-hour limit", TimeSpan.FromHours(5)),
        ("limit_7d", "weekly", "Weekly limit", TimeSpan.FromDays(7)),
        ("limit_month_total", "monthly", "Monthly limit", null),
    ];

    public static ProviderSnapshot Parse(ReadOnlySpan<byte> response, DateTimeOffset capturedAt, string? plan = null)
    {
        if (response.IsEmpty)
        {
            throw InvalidResponse("Kimi Code returned an empty usage response.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response.ToArray(), DocumentOptions);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("usages", out JsonElement usages)
                || usages.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse("Kimi Code returned usage that UsageDeck could not read. Update Kimi Code and UsageDeck, then refresh.");
            }

            List<UsageWindow> windows = [];
            foreach ((string property, string id, string displayName, TimeSpan? duration) in Windows)
            {
                if (!usages.TryGetProperty(property, out JsonElement entry)
                    || entry.ValueKind != JsonValueKind.Object
                    || ReadRatio(entry) is not double ratio)
                {
                    continue;
                }

                windows.Add(new UsageWindow(
                    id,
                    displayName,
                    ratio * 100,
                    ReadResetTime(entry),
                    duration));
            }

            // An account without a usage-limited plan legitimately reports no windows, which
            // the usage window shows as "No rate limits reported".
            return new ProviderSnapshot(
                ProviderId.KimiCode,
                ProviderId.KimiCode.DisplayName,
                SourceDescription,
                capturedAt,
                UsageDataState.Fresh,
                windows,
                string.IsNullOrWhiteSpace(plan) ? null : new AccountIdentity(null, plan.Trim()),
                coverage: UsageDataCoverage.SignedInAccount);
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Kimi Code returned usage data that UsageDeck could not read.",
                exception);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Kimi Code returned usage values outside the supported range.",
                exception);
        }
    }

    /// <summary>
    /// Reads the plan name from Kimi Code's account profile. Nothing else from the profile is
    /// kept, and an unreadable profile yields null rather than failing the usage reading.
    /// </summary>
    public static string? ParsePlan(ReadOnlySpan<byte> response)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(response.ToArray(), DocumentOptions);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("user_level_name", out JsonElement level)
                && level.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(level.GetString())
                    ? level.GetString()!.Trim()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? ReadRatio(JsonElement entry)
    {
        if (!entry.TryGetProperty("used_ratio", out JsonElement value))
        {
            return null;
        }

        double ratio;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
        {
            ratio = number;
        }
        else if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            ratio = parsed;
        }
        else
        {
            return null;
        }

        return double.IsFinite(ratio) ? Math.Clamp(ratio, 0, 1) : null;
    }

    private static DateTimeOffset? ReadResetTime(JsonElement entry) =>
        entry.TryGetProperty("reset_time", out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset resetsAt)
                ? resetsAt
                : null;

    private static ProviderException InvalidResponse(string safeMessage) =>
        new(ProviderErrorCategory.InvalidResponse, safeMessage);
}
