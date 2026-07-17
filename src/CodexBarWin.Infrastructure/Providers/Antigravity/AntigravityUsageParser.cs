using System.Globalization;
using System.Text.RegularExpressions;
using CodexBarWin.Core.Providers;

namespace CodexBarWin.Infrastructure.Providers.Antigravity;

public static partial class AntigravityUsageParser
{
    public static IReadOnlyList<UsageWindow> Parse(string terminalOutput, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalOutput);

        string clean = StripTerminalSequences(terminalOutput);
        string[] lines = clean
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        List<UsageWindow> windows = [];
        Dictionary<string, int> windowIndexes = new(StringComparer.OrdinalIgnoreCase);
        string? group = null;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            group = DetectGroup(line) ?? group;

            Match percent = PercentRegex().Match(line);
            if (!percent.Success
                || !double.TryParse(
                    percent.Groups["value"].Value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out double value))
            {
                continue;
            }

            string label = CleanLabel(line[..percent.Index]);
            if (!IsUsefulLabel(label))
            {
                label = FindPreviousLabel(lines, index) ?? group ?? string.Empty;
            }

            if (!IsUsefulLabel(label))
            {
                continue;
            }

            string displayName = DisplayName(label, group);
            string id = Slug(displayName);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            string qualifier = percent.Groups["qualifier"].Value;
            double usedPercent = qualifier.Equals("used", StringComparison.OrdinalIgnoreCase)
                ? value
                : 100 - value;
            string resetSection = string.Join('\n', lines.Skip(index).Take(4));
            UsageWindow window = new(
                id,
                displayName,
                usedPercent,
                TryParseReset(resetSection, now),
                confidence: UsageConfidence.Parsed);

            if (windowIndexes.TryGetValue(id, out int existingIndex))
            {
                windows[existingIndex] = window;
            }
            else
            {
                windowIndexes[id] = windows.Count;
                windows.Add(window);
            }
        }

        if (windows.Count > 0)
        {
            return windows;
        }

        if (AuthenticationRegex().IsMatch(clean))
        {
            throw new ProviderException(
                ProviderErrorCategory.AuthenticationRequired,
                "Antigravity needs you to sign in. Run `agy`, sign in, then refresh.");
        }

        throw new ProviderException(
            ProviderErrorCategory.InvalidResponse,
            "Antigravity opened, but its model quota panel could not be read.");
    }

    public static string StripTerminalSequences(string value)
    {
        string clean = OscRegex().Replace(value, string.Empty);
        clean = CursorCsiRegex().Replace(clean, "\n");
        clean = CsiRegex().Replace(clean, string.Empty);
        clean = clean.Replace('\r', '\n');
        return RepeatedNewlineRegex().Replace(clean, "\n");
    }

    private static string? DetectGroup(string line)
    {
        if (line.Contains("Gemini", StringComparison.OrdinalIgnoreCase)
            && line.Contains("model", StringComparison.OrdinalIgnoreCase))
        {
            return "Gemini";
        }

        if (line.Contains("Claude", StringComparison.OrdinalIgnoreCase)
            && line.Contains("GPT", StringComparison.OrdinalIgnoreCase))
        {
            return "Claude + GPT";
        }

        return null;
    }

    private static string? FindPreviousLabel(string[] lines, int percentLineIndex)
    {
        for (int index = percentLineIndex - 1; index >= Math.Max(0, percentLineIndex - 4); index--)
        {
            string candidate = CleanLabel(lines[index]);
            if (IsUsefulLabel(candidate)
                && !candidate.Contains("quota", StringComparison.OrdinalIgnoreCase)
                && !candidate.StartsWith("reset", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string CleanLabel(string value)
    {
        string clean = DecorationRegex().Replace(value, " ");
        clean = PercentBarRegex().Replace(clean, " ");
        clean = WhitespaceRegex().Replace(clean, " ").Trim(' ', ':', '-', '–', '—');
        return clean;
    }

    private static bool IsUsefulLabel(string label) =>
        label.Length is >= 2 and <= 80
        && label.Any(char.IsLetter)
        && !label.Equals("remaining", StringComparison.OrdinalIgnoreCase)
        && !label.Equals("used", StringComparison.OrdinalIgnoreCase)
        && !label.Equals("available", StringComparison.OrdinalIgnoreCase)
        && !label.Contains("press ", StringComparison.OrdinalIgnoreCase)
        && !label.Contains("model quota", StringComparison.OrdinalIgnoreCase);

    private static string DisplayName(string label, string? group)
    {
        bool isWeekly = label.Contains("week", StringComparison.OrdinalIgnoreCase);
        bool isSession = label.Contains("session", StringComparison.OrdinalIgnoreCase)
            || FiveHourRegex().IsMatch(label);
        if (group is not null && (isWeekly || isSession))
        {
            return isWeekly ? $"{group} weekly" : $"{group} session";
        }

        return label.Trim();
    }

    private static string Slug(string value) =>
        NonAlphaNumericRegex().Replace(value.ToLowerInvariant(), "-").Trim('-');

    private static DateTimeOffset? TryParseReset(string section, DateTimeOffset now)
    {
        Match iso = IsoTimestampRegex().Match(section);
        if (iso.Success
            && DateTimeOffset.TryParse(
                iso.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset timestamp))
        {
            return timestamp;
        }

        Match relative = RelativeResetRegex().Match(section);
        if (relative.Success)
        {
            int days = ParseInt(relative.Groups["days"].Value);
            int hours = ParseInt(relative.Groups["hours"].Value);
            int minutes = ParseInt(relative.Groups["minutes"].Value);
            TimeSpan duration = TimeSpan.FromDays(days) + TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
            return duration > TimeSpan.Zero ? now.Add(duration) : null;
        }

        return null;
    }

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) ? result : 0;

    [GeneratedRegex("\\x1B\\][^\\x07]*(?:\\x07|\\x1B\\\\)")]
    private static partial Regex OscRegex();

    [GeneratedRegex("\\x1B\\[[0-9;?]*[HfABCDJK]")]
    private static partial Regex CursorCsiRegex();

    [GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex CsiRegex();

    [GeneratedRegex("\\n{2,}")]
    private static partial Regex RepeatedNewlineRegex();

    [GeneratedRegex("(?<value>\\d{1,3}(?:\\.\\d+)?)\\s*%\\s*(?<qualifier>used|remaining|left|available)?", RegexOptions.IgnoreCase)]
    private static partial Regex PercentRegex();

    [GeneratedRegex("[│┃┌┐└┘├┤┬┴┼─━═█▓▒░■●○◉]+")]
    private static partial Regex DecorationRegex();

    [GeneratedRegex("[=\\[\\]<>]+")]
    private static partial Regex PercentBarRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("(?:5|five)[- ]?hour", RegexOptions.IgnoreCase)]
    private static partial Regex FiveHourRegex();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex("\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d+)?Z", RegexOptions.IgnoreCase)]
    private static partial Regex IsoTimestampRegex();

    [GeneratedRegex("resets?\\s+in(?:\\s+(?<days>\\d+)\\s*d(?:ays?)?)?(?:\\s+(?<hours>\\d+)\\s*h(?:ours?)?)?(?:\\s+(?<minutes>\\d+)\\s*m(?:in(?:utes?)?)?)?", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeResetRegex();

    [GeneratedRegex("(?:sign|log)\\s*in|authentication required|choose an account", RegexOptions.IgnoreCase)]
    private static partial Regex AuthenticationRegex();
}
