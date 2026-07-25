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

        // Claude Code repaints the /usage panel while it is open, and cursor-movement sequences
        // are stripped rather than replayed, so an overwritten frame survives in the capture as
        // extra text. Keep one window per limit, taking the last (most settled) frame's values.
        List<UsageWindow> windows = [];
        Dictionary<string, int> windowIndicesById = new(StringComparer.Ordinal);
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

            UsageWindow window = new(
                id,
                displayName,
                usedPercent,
                resetsAt,
                confidence: UsageConfidence.Parsed);

            if (windowIndicesById.TryGetValue(id, out int existingIndex))
            {
                windows[existingIndex] = window;
            }
            else
            {
                windowIndicesById[id] = windows.Count;
                windows.Add(window);
            }
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

    // Claude Code sometimes lays a frame out entirely with cursor positioning rather than literal
    // spaces, and those sequences are stripped rather than replayed, so a capture can arrive with
    // no whitespace at all. Compare against a compacted copy wherever a marker has to survive
    // either rendering.
    public static string Compact(string value) =>
        WhitespaceRegex().Replace(value, string.Empty);

    private static bool IsQuotaUnavailable(string clean)
    {
        string compact = Compact(clean);
        bool subscriptionNotice = compact.Contains(
            "currentlyusingyoursubscriptiontopoweryourclaudecodeusage",
            StringComparison.OrdinalIgnoreCase);
        bool costOnly = compact.Contains("totalcost:", StringComparison.OrdinalIgnoreCase)
            && !compact.Contains("currentsession", StringComparison.OrdinalIgnoreCase);
        return subscriptionNotice || costOnly;
    }

    private static bool IsSessionLabel(string label) =>
        label.Contains("session", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllModelsLabel(string label) =>
        Compact(label).Contains("allmodels", StringComparison.OrdinalIgnoreCase);

    private static string Id(string label)
    {
        if (IsSessionLabel(label))
        {
            return "session";
        }

        if (IsAllModelsLabel(label))
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

        if (IsAllModelsLabel(label))
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

        string text = match.Groups["value"].Value.Trim();
        string[] formats =
        [
            "hhtt", "htt", "h:mmtt",
            "MMM d 'at' hhtt", "MMM d 'at' htt", "MMM d 'at' h:mmtt",
            "MMM d, hhtt", "MMM d, htt", "MMM d, h:mmtt",
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

        // A leading month name means the reset carries a date; anything else is a bare time of
        // day. Only an explicit four-digit year is absolute - a bare "Jul 29" has to be rolled
        // forward relative to `now` rather than inheriting the ambient current year.
        DateTime candidate;
        if (match.Groups["year"].Success)
        {
            candidate = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        }
        else if (char.IsLetter(text[0]))
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

    // Every gap is \s* rather than \s+ because a frame padded with cursor movement instead of
    // literal spaces loses all of its whitespace once the escape sequences are stripped, leaving
    // labels like "Currentweek(allmodels)".
    [GeneratedRegex("Current\\s*(?:session|week\\s*\\([^\\r\\n)]+\\))", RegexOptions.IgnoreCase)]
    private static partial Regex UsageLabelRegex();

    [GeneratedRegex("(?<value>\\d{1,3}(?:\\.\\d+)?)\\s*%\\s*(?<qualifier>used|left|remaining|available)", RegexOptions.IgnoreCase)]
    private static partial Regex PercentRegex();

    [GeneratedRegex("Current\\s*week\\s*\\((?<model>[^)]+)\\)", RegexOptions.IgnoreCase)]
    private static partial Regex WeeklyModelRegex();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex NonAlphaNumericRegex();

    // The panel is drawn with cursor positioning, so a stripped capture puts every row - and any
    // trailing banner - on one line. Match the date/time itself instead of the rest of the line,
    // otherwise a weekly reset swallows whatever the CLI painted after it. Every gap is \s* rather
    // than \s+ because rows aligned with cursor movement instead of literal spaces (seen on the
    // per-model weekly row) lose all their whitespace when the escape sequences are stripped.
    [GeneratedRegex(
        "Resets\\s*(?<value>(?:[A-Za-z]{3,9}\\s*\\d{1,2}(?:,\\s*(?<year>\\d{4}))?\\s*(?:,|at)\\s*)?\\d{1,2}(?::\\d{2})?\\s*[ap]m)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ResetRegex();
}
