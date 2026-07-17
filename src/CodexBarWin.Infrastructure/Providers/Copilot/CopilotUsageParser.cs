using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexBarWin.Core.Providers;

namespace CodexBarWin.Infrastructure.Providers.Copilot;

public static partial class CopilotUsageParser
{
    private static readonly string[] PreferredQuotaOrder =
        ["premium_interactions", "chat", "completions"];

    public static ProviderSnapshot Parse(string json, DateTimeOffset capturedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                MaxDepth = 32,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse();
            }

            if (TryGetString(root, "message") is string message)
            {
                ProviderErrorCategory category = message.Contains("auth", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("credential", StringComparison.OrdinalIgnoreCase)
                    ? ProviderErrorCategory.AuthenticationRequired
                    : ProviderErrorCategory.Unavailable;
                string safeMessage = category == ProviderErrorCategory.AuthenticationRequired
                    ? "GitHub needs you to sign in. Run `gh auth login`, then refresh."
                    : "GitHub could not return Copilot usage.";
                throw new ProviderException(category, safeMessage);
            }

            string? plan = TryGetString(root, "copilot_plan");
            DateTimeOffset? defaultReset = ParseReset(root);
            List<UsageWindow> windows = ParseQuotaSnapshots(root, defaultReset);
            if (windows.Count == 0)
            {
                windows.AddRange(ParseLimitedUserQuotas(root, defaultReset));
            }

            if (windows.Count == 0)
            {
                throw new ProviderException(
                    ProviderErrorCategory.Unavailable,
                    "GitHub did not expose Copilot quota windows for this account.");
            }

            return new ProviderSnapshot(
                ProviderId.Copilot,
                "GitHub Copilot",
                "GitHub CLI",
                capturedAt,
                UsageDataState.Fresh,
                windows,
                new AccountIdentity(null, plan));
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "GitHub returned Copilot usage in an unexpected format.",
                exception);
        }
    }

    private static List<UsageWindow> ParseQuotaSnapshots(JsonElement root, DateTimeOffset? defaultReset)
    {
        if (!root.TryGetProperty("quota_snapshots", out JsonElement snapshots)
            || snapshots.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<string> names = PreferredQuotaOrder
            .Where(name => snapshots.TryGetProperty(name, out _))
            .Concat(snapshots.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => !PreferredQuotaOrder.Contains(name, StringComparer.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        List<UsageWindow> windows = [];
        foreach (string name in names)
        {
            JsonElement quota = snapshots.GetProperty(name);
            if (quota.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            bool unlimited = TryGetBoolean(quota, "unlimited") == true;
            bool? hasQuota = TryGetBoolean(quota, "has_quota");
            if (hasQuota == false && !unlimited)
            {
                continue;
            }

            DateTimeOffset? reset = ParseReset(quota) ?? defaultReset;
            string displayName = DisplayName(name);
            if (unlimited)
            {
                windows.Add(new UsageWindow(
                    name,
                    displayName,
                    0,
                    reset,
                    isUnlimited: true));
                continue;
            }

            double? remainingPercent = TryGetDouble(quota, "percent_remaining");
            double? entitlement = TryGetDouble(quota, "entitlement");
            double? remaining = TryGetDouble(quota, "remaining");
            if (remainingPercent is null && entitlement > 0 && remaining is not null)
            {
                remainingPercent = remaining.Value / entitlement.Value * 100;
            }

            if (remainingPercent is null || !double.IsFinite(remainingPercent.Value))
            {
                continue;
            }

            windows.Add(new UsageWindow(
                name,
                displayName,
                100 - remainingPercent.Value,
                reset));
        }

        return windows;
    }

    private static List<UsageWindow> ParseLimitedUserQuotas(
        JsonElement root,
        DateTimeOffset? defaultReset)
    {
        if (!root.TryGetProperty("monthly_quotas", out JsonElement monthly)
            || monthly.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("limited_user_quotas", out JsonElement remaining)
            || remaining.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<UsageWindow> windows = [];
        foreach (JsonProperty quota in monthly.EnumerateObject())
        {
            if (!TryReadNumber(quota.Value, out double entitlement)
                || entitlement <= 0
                || !remaining.TryGetProperty(quota.Name, out JsonElement remainingValue)
                || !TryReadNumber(remainingValue, out double amountRemaining))
            {
                continue;
            }

            windows.Add(new UsageWindow(
                quota.Name,
                DisplayName(quota.Name),
                100 - (amountRemaining / entitlement * 100),
                defaultReset));
        }

        return windows;
    }

    private static DateTimeOffset? ParseReset(JsonElement element)
    {
        foreach (string propertyName in new[]
                 {
                     "quota_reset_at",
                     "quota_reset_date_utc",
                     "quota_reset_date",
                     "limited_user_reset_date",
                 })
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    value.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset timestamp))
            {
                return timestamp;
            }

            if (TryReadNumber(value, out double epochSeconds) && epochSeconds > 0)
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeSeconds((long)epochSeconds);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static string DisplayName(string name) => name.ToLowerInvariant() switch
    {
        "premium_interactions" => "Premium requests",
        "chat" => "Chat",
        "completions" => "Code completions",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            NonAlphaNumericRegex().Replace(name, " ").Trim().ToLowerInvariant()),
    };

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? TryGetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static double? TryGetDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && TryReadNumber(value, out double result)
            ? result
            : null;

    private static bool TryReadNumber(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static ProviderException InvalidResponse() => new(
        ProviderErrorCategory.InvalidResponse,
        "GitHub returned Copilot usage in an unexpected format.");

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex NonAlphaNumericRegex();
}
