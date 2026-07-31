using System.Globalization;
using System.Text.Json;
using UsageDeck.Core.Providers;

namespace UsageDeck.Infrastructure.Providers.TheClawBay;

public static class TheClawBayUsageParser
{
    public static ProviderSnapshot Parse(ReadOnlySpan<byte> response, string sourceDescription)
    {
        if (response.IsEmpty)
        {
            throw InvalidResponse("TheClawBay returned an empty usage response.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse("TheClawBay returned an unsupported usage response.");
            }

            DateTimeOffset observedAt = ReadObservedAt(root);
            if (!root.TryGetProperty("usage", out JsonElement usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse("TheClawBay returned incomplete usage data.");
            }

            return new ProviderSnapshot(
                ProviderId.TheClawBay,
                ProviderId.TheClawBay.DisplayName,
                sourceDescription,
                observedAt,
                UsageDataState.Fresh,
                [
                    ParseWindow(usage, "fiveHour", "five-hour", "5-hour", TimeSpan.FromHours(5), observedAt),
                    ParseWindow(usage, "weekly", "weekly", "Weekly", TimeSpan.FromDays(7), observedAt),
                ]);
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "TheClawBay returned usage data that UsageDeck could not read.",
                exception);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "TheClawBay returned usage data that UsageDeck could not read.",
                exception);
        }
    }

    private static DateTimeOffset ReadObservedAt(JsonElement root)
    {
        if (root.TryGetProperty("observedAt", out JsonElement observedAt)
            && observedAt.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                observedAt.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsed))
        {
            return parsed;
        }

        throw InvalidResponse("TheClawBay returned usage without an observation time.");
    }

    private static UsageWindow ParseWindow(
        JsonElement usage,
        string propertyName,
        string id,
        string displayName,
        TimeSpan duration,
        DateTimeOffset observedAt)
    {
        if (!usage.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse("TheClawBay returned incomplete usage data.");
        }

        double usedPercent = ReadFiniteDouble(value, "progressPercentUsed")
            ?? ReadFiniteDouble(value, "percentUsed")
            ?? throw InvalidResponse("TheClawBay returned usage without a percentage.");
        DateTimeOffset resetsAt = ReadReset(value, observedAt);
        return new UsageWindow(
            id,
            displayName,
            usedPercent,
            resetsAt,
            duration,
            UsageConfidence.Authoritative);
    }

    private static double? ReadFiniteDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double parsed)
        && double.IsFinite(parsed)
            ? parsed
            : null;

    private static DateTimeOffset ReadReset(JsonElement window, DateTimeOffset observedAt)
    {
        if (window.TryGetProperty("windowEnd", out JsonElement end))
        {
            if (end.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    end.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset parsed))
            {
                return parsed;
            }

            throw InvalidResponse("TheClawBay returned an invalid reset time.");
        }

        double? seconds = ReadFiniteDouble(window, "secondsUntilReset");
        if (seconds is null or < 0)
        {
            throw InvalidResponse("TheClawBay returned usage without a reset time.");
        }

        return observedAt.AddSeconds(seconds.Value);
    }

    private static ProviderException InvalidResponse(string safeMessage) =>
        new(ProviderErrorCategory.InvalidResponse, safeMessage);
}
