using System.Globalization;
using System.Text.RegularExpressions;
using UsageDeck.Core.Providers;

namespace UsageDeck.Infrastructure.Providers.Claude;

public static partial class ClaudeUsageParser
{
    public static IReadOnlyList<UsageWindow> Parse(string terminalOutput, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalOutput);

        string clean = StripTerminalSequences(terminalOutput);
        if (IsQuotaUnavailable(clean))
        {
            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                "Claude did not expose subscription quota windows for this account.");
        }

        MatchCollection labels = UsageLabelRegex().Matches(clean);
        if (labels.Count == 0 || !labels.Any(match => IsSessionLabel(match.Value)))
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Claude opened, but its usage panel could not be read.");
        }

        List<UsageWindow> windows = [];
        for (int index = 0; index < labels.Count; index++)
        {
            Match label = labels[index];
            int end = index + 1 < labels.Count ? labels[index + 1].Index : clean.Length;
            string section = clean[label.Index..end];
            Match percent = PercentRegex().Match(section);
            if (!percent.Success
                || !double.TryParse(percent.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out double value))
            {
                continue;
            }

            string qualifier = percent.Groups["qualifier"].Value;
            double usedPercent = qualifier.Equals("used", StringComparison.OrdinalIgnoreCase) ? value : 100 - value;
            string displayName = DisplayName(label.Value);
            string id = Id(label.Value);
            DateTimeOffset? resetsAt = TryParseReset(section, now);

            windows.Add(new UsageWindow(
                id,
                displayName,
                usedPercent,
                resetsAt,
                confidence: UsageConfidence.Parsed));
        }

        if (!windows.Any(window => window.Id == "session"))
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Claude opened, but its session usage value could not be read.");
        }

        return windows;
    }

    public static string StripTerminalSequences(string value)
    {
        string clean = OscRegex().Replace(value, string.Empty);
        clean = CsiRegex().Replace(clean, string.Empty);
        clean = clean.Replace('\r', '\n');
        return RepeatedNewlineRegex().Replace(clean, "\n\n");
    }

    private static bool IsQuotaUnavailable(string clean)
    {
        string compact = WhitespaceRegex().Replace(clean, string.Empty);
        bool subscriptionNotice = compact.Contains(
            "currentlyusingyoursubscriptiontopoweryourclaudecodeusage",
            StringComparison.OrdinalIgnoreCase);
        bool costOnly = clean.Contains("Total cost:", StringComparison.OrdinalIgnoreCase)
            && !clean.Contains("Current session", StringComparison.OrdinalIgnoreCase);
        return subscriptionNotice || costOnly;
    }

    private static bool IsSessionLabel(string label) =>
        label.Contains("session", StringComparison.OrdinalIgnoreCase);

    private static string Id(string label)
    {
        if (IsSessionLabel(label))
        {
            return "session";
        }

        if (label.Contains("all models", StringComparison.OrdinalIgnoreCase))
        {
            return "weekly";
        }

        string model = WeeklyModelRegex().Match(label).Groups["model"].Value.Trim();
        string slug = NonAlphaNumericRegex().Replace(model.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "weekly-model" : $"weekly-{slug}";
    }

    private static string DisplayName(string label)
    {
        if (IsSessionLabel(label))
        {
            return "Current session";
        }

        if (label.Contains("all models", StringComparison.OrdinalIgnoreCase))
        {
            return "Weekly limit";
        }

        string model = WeeklyModelRegex().Match(label).Groups["model"].Value.Trim();
        return string.IsNullOrEmpty(model) ? "Weekly model limit" : $"{model} weekly";
    }

    private static DateTimeOffset? TryParseReset(string section, DateTimeOffset now)
    {
        Match match = ResetRegex().Match(section);
        if (!match.Success)
        {
            return null;
        }

        string text = ParenthesisedTimezoneRegex().Replace(match.Groups["value"].Value, string.Empty).Trim();
        string[] formats =
        [
            "hhtt", "htt", "h:mmtt",
            "MMM d 'at' hhtt", "MMM d 'at' htt", "MMM d 'at' h:mmtt",
            "MMM d, yyyy, hhtt", "MMM d, yyyy, htt", "MMM d, yyyy, h:mmtt",
        ];

        if (!DateTime.TryParseExact(
                text.Replace(" ", string.Empty),
                formats.Select(format => format.Replace(" ", string.Empty)).ToArray(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out DateTime parsed))
        {
            return null;
        }

        DateTime candidate;
        if (text.Any(char.IsLetter) && text.Contains(',', StringComparison.Ordinal))
        {
            candidate = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        }
        else if (text.Any(char.IsLetter) && text.Contains(" at ", StringComparison.OrdinalIgnoreCase))
        {
            candidate = new DateTime(now.Year, parsed.Month, parsed.Day, parsed.Hour, parsed.Minute, 0);
            if (candidate <= now.LocalDateTime)
            {
                candidate = candidate.AddYears(1);
            }
        }
        else
        {
            candidate = new DateTime(now.Year, now.Month, now.Day, parsed.Hour, parsed.Minute, 0);
            if (candidate <= now.LocalDateTime)
            {
                candidate = candidate.AddDays(1);
            }
        }

        return new DateTimeOffset(candidate, TimeZoneInfo.Local.GetUtcOffset(candidate));
    }

    [GeneratedRegex("\\x1B\\][^\\x07]*(?:\\x07|\\x1B\\\\)")]
    private static partial Regex OscRegex();

    [GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex CsiRegex();

    [GeneratedRegex("\\n{3,}")]
    private static partial Regex RepeatedNewlineRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("Current\\s+(?:session|week\\s*\\([^\\r\\n)]+\\))", RegexOptions.IgnoreCase)]
    private static partial Regex UsageLabelRegex();

    [GeneratedRegex("(?<value>\\d{1,3}(?:\\.\\d+)?)\\s*%\\s*(?<qualifier>used|left|remaining|available)", RegexOptions.IgnoreCase)]
    private static partial Regex PercentRegex();

    [GeneratedRegex("Current\\s+week\\s*\\((?<model>[^)]+)\\)", RegexOptions.IgnoreCase)]
    private static partial Regex WeeklyModelRegex();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex("Resets\\s+(?<value>[^\\r\\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResetRegex();

    [GeneratedRegex("\\s*\\([^)]+\\)\\s*$")]
    private static partial Regex ParenthesisedTimezoneRegex();
}
