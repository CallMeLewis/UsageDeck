using System.Text.Json;
using CodexBarWin.Core.Providers;

namespace CodexBarWin.Infrastructure.Providers.Zai;

public static class ZaiUsageParser
{
    public static ProviderSnapshot Parse(ReadOnlySpan<byte> response, DateTimeOffset capturedAt)
    {
        if (response.IsEmpty)
        {
            throw InvalidResponse("Z.AI returned an empty usage response.");
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
                throw InvalidResponse("Z.AI returned an unsupported usage response.");
            }

            int code = TryGetInt32(root, "code") ?? 0;
            bool success = TryGetBoolean(root, "success") ?? false;
            if (!success || code != 200)
            {
                string? message = TryGetString(root, "msg");
                bool authenticationFailure = code is 401 or 403 or 1001
                    || message?.Contains("auth", StringComparison.OrdinalIgnoreCase) == true
                    || message?.Contains("token", StringComparison.OrdinalIgnoreCase) == true;
                throw new ProviderException(
                    authenticationFailure
                        ? ProviderErrorCategory.AuthenticationRequired
                        : ProviderErrorCategory.Unavailable,
                    authenticationFailure
                        ? "Z.AI rejected the API key. Check it in Settings, then refresh."
                        : "Z.AI could not return Coding Plan usage right now.");
            }

            if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse("Z.AI returned usage without a data section.");
            }

            string? plan = FirstNonEmptyString(data, "planName", "plan", "plan_type", "packageName", "level");
            if (!data.TryGetProperty("limits", out JsonElement limits) || limits.ValueKind != JsonValueKind.Array)
            {
                throw InvalidResponse("Z.AI returned usage without any plan limits.");
            }

            List<ParsedLimit> tokenLimits = [];
            List<ParsedLimit> timeLimits = [];
            foreach (JsonElement limit in limits.EnumerateArray())
            {
                if (limit.ValueKind != JsonValueKind.Object || !TryParseLimit(limit, out ParsedLimit parsed))
                {
                    continue;
                }

                if (string.Equals(parsed.Type, "TOKENS_LIMIT", StringComparison.Ordinal))
                {
                    tokenLimits.Add(parsed);
                }
                else if (string.Equals(parsed.Type, "TIME_LIMIT", StringComparison.Ordinal))
                {
                    timeLimits.Add(parsed);
                }
            }

            if (tokenLimits.Count == 0 && timeLimits.Count == 0)
            {
                throw InvalidResponse("Z.AI returned no recognised Coding Plan limits.");
            }

            List<UsageWindow> windows = [];
            int index = 0;
            foreach (ParsedLimit limit in tokenLimits.OrderBy(limit => limit.Duration ?? TimeSpan.MaxValue))
            {
                windows.Add(new UsageWindow(
                    $"tokens-{index++}",
                    TokenWindowName(limit.Duration),
                    limit.UsedPercent,
                    limit.ResetsAt,
                    limit.Duration,
                    UsageConfidence.Authoritative,
                    limit.UsageKnown));
            }

            foreach (ParsedLimit limit in timeLimits)
            {
                windows.Add(new UsageWindow(
                    $"mcp-{index++}",
                    "MCP tools",
                    limit.UsedPercent,
                    limit.ResetsAt,
                    duration: null,
                    UsageConfidence.Authoritative,
                    limit.UsageKnown));
            }

            return new ProviderSnapshot(
                ProviderId.Zai,
                ProviderId.Zai.DisplayName,
                "Z.AI Coding Plan API",
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
                "Z.AI returned usage data that CodexBar could not read.",
                exception);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Z.AI returned usage values outside the supported range.",
                exception);
        }
    }

    private static bool TryParseLimit(JsonElement element, out ParsedLimit parsed)
    {
        parsed = null!;
        string? type = TryGetString(element, "type");
        if (type is not ("TOKENS_LIMIT" or "TIME_LIMIT"))
        {
            return false;
        }

        int unit = TryGetInt32(element, "unit") ?? 0;
        int number = TryGetInt32(element, "number") ?? 0;
        TimeSpan? duration = Duration(unit, number);
        double? quota = TryGetDouble(element, "usage");
        double? current = TryGetDouble(element, "currentValue");
        double? remaining = TryGetDouble(element, "remaining");
        double? percentage = TryGetDouble(element, "percentage");
        double usedPercent;
        bool usageKnown;
        if (quota is > 0 && (current is not null || remaining is not null))
        {
            double usedFromRemaining = remaining is null ? 0 : quota.Value - remaining.Value;
            double used = current is null ? usedFromRemaining : Math.Max(usedFromRemaining, current.Value);
            usedPercent = Math.Clamp(used / quota.Value * 100, 0, 100);
            usageKnown = true;
        }
        else if (percentage is not null && double.IsFinite(percentage.Value))
        {
            usedPercent = Math.Clamp(percentage.Value, 0, 100);
            usageKnown = true;
        }
        else
        {
            usedPercent = 0;
            usageKnown = false;
        }

        DateTimeOffset? resetsAt = null;
        long? resetMilliseconds = TryGetInt64(element, "nextResetTime");
        if (resetMilliseconds is >= 0 and <= 253402300799999)
        {
            resetsAt = DateTimeOffset.FromUnixTimeMilliseconds(resetMilliseconds.Value);
        }

        parsed = new ParsedLimit(type, duration, usedPercent, usageKnown, resetsAt);
        return true;
    }

    private static TimeSpan? Duration(int unit, int number)
    {
        if (number <= 0)
        {
            return null;
        }

        return unit switch
        {
            1 => TimeSpan.FromDays(number),
            3 => TimeSpan.FromHours(number),
            5 => TimeSpan.FromMinutes(number),
            6 => TimeSpan.FromDays(number * 7d),
            _ => null,
        };
    }

    private static string TokenWindowName(TimeSpan? duration)
    {
        if (duration == TimeSpan.FromHours(5))
        {
            return "5-hour";
        }

        if (duration == TimeSpan.FromDays(7))
        {
            return "Weekly";
        }

        if (duration is not null && duration.Value.TotalDays is >= 28 and <= 31)
        {
            return "Monthly";
        }

        if (duration is null)
        {
            return "Token limit";
        }

        if (duration.Value.TotalHours < 24)
        {
            return $"{duration.Value.TotalHours:0.#}-hour";
        }

        return $"{duration.Value.TotalDays:0.#}-day";
    }

    private static string? FirstNonEmptyString(JsonElement element, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            string? value = TryGetString(element, propertyName)?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool? TryGetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? TryGetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int parsed)
                ? parsed
                : null;

    private static long? TryGetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long parsed)
                ? parsed
                : null;

    private static double? TryGetDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double parsed)
            && double.IsFinite(parsed)
                ? parsed
                : null;

    private static ProviderException InvalidResponse(string safeMessage) =>
        new(ProviderErrorCategory.InvalidResponse, safeMessage);

    private sealed record ParsedLimit(
        string Type,
        TimeSpan? Duration,
        double UsedPercent,
        bool UsageKnown,
        DateTimeOffset? ResetsAt);
}
