using System.Globalization;
using System.Text.RegularExpressions;
using CodexBarWin.Core.Providers;

namespace CodexBarWin.Infrastructure.Providers.Kiro;

public static partial class KiroUsageParser
{
    public static ProviderSnapshot Parse(string terminalOutput, DateTimeOffset capturedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalOutput);

        string clean = StripTerminalSequences(terminalOutput);
        if (AuthenticationRegex().IsMatch(clean))
        {
            throw new ProviderException(
                ProviderErrorCategory.AuthenticationRequired,
                "Kiro needs you to sign in. Run `kiro-cli login`, then refresh.");
        }

        string? plan = ParsePlan(clean);
        List<UsageWindow> windows = [];

        Match credits = CreditsRegex().Match(clean);
        if (credits.Success
            && TryParseNumber(credits.Groups["used"].Value, out double creditsUsed)
            && TryParseNumber(credits.Groups["total"].Value, out double creditsTotal)
            && creditsTotal > 0)
        {
            windows.Add(new UsageWindow(
                "plan-credits",
                "Plan credits",
                creditsUsed / creditsTotal * 100,
                ParseReset(clean, capturedAt),
                confidence: UsageConfidence.Parsed));
        }
        else if (TryParseDisplayedPercentage(clean, out double displayedPercentage))
        {
            windows.Add(new UsageWindow(
                "plan-credits",
                "Plan credits",
                displayedPercentage,
                ParseReset(clean, capturedAt),
                confidence: UsageConfidence.Parsed));
        }

        Match bonus = BonusCreditsRegex().Match(clean);
        if (bonus.Success
            && TryParseNumber(bonus.Groups["used"].Value, out double bonusUsed)
            && TryParseNumber(bonus.Groups["total"].Value, out double bonusTotal)
            && bonusTotal > 0)
        {
            DateTimeOffset? expiresAt = int.TryParse(
                bonus.Groups["days"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int days)
                    ? capturedAt.AddDays(days)
                    : null;
            windows.Add(new UsageWindow(
                "bonus-credits",
                "Bonus credits",
                bonusUsed / bonusTotal * 100,
                expiresAt,
                confidence: UsageConfidence.Parsed));
        }

        bool isManagedPlan = ManagedPlanRegex().IsMatch(clean);
        if (windows.Count == 0 && !isManagedPlan)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Kiro opened, but its usage details could not be read.");
        }

        return new ProviderSnapshot(
            ProviderId.Kiro,
            ProviderId.Kiro.DisplayName,
            "Kiro CLI",
            capturedAt,
            UsageDataState.Fresh,
            windows,
            plan is null ? null : new AccountIdentity(null, plan));
    }

    public static string StripTerminalSequences(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string clean = OscRegex().Replace(value, string.Empty);
        clean = CursorCsiRegex().Replace(clean, "\n");
        clean = CsiRegex().Replace(clean, string.Empty);
        clean = clean.Replace('\r', '\n');
        return RepeatedNewlineRegex().Replace(clean, "\n").Trim();
    }

    private static string? ParsePlan(string clean)
    {
        Match estimatedUsage = EstimatedUsagePlanRegex().Match(clean);
        if (estimatedUsage.Success)
        {
            return CleanPlan(estimatedUsage.Groups["plan"].Value);
        }

        Match labelled = LabelledPlanRegex().Match(clean);
        if (labelled.Success)
        {
            return CleanPlan(labelled.Groups["plan"].Value);
        }

        Match legacy = LegacyPlanRegex().Match(clean);
        return legacy.Success ? CleanPlan(legacy.Groups["plan"].Value) : null;
    }

    private static string? CleanPlan(string value)
    {
        string plan = WhitespaceRegex().Replace(value, " ").Trim(' ', '|', ':', '-');
        return plan.Length is > 0 and <= 80 ? plan : null;
    }

    private static bool TryParseDisplayedPercentage(string clean, out double percentage)
    {
        Match match = DisplayedPercentageRegex().Match(clean);
        if (match.Success
            && double.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out percentage))
        {
            return true;
        }

        percentage = 0;
        return false;
    }

    private static DateTimeOffset? ParseReset(string clean, DateTimeOffset now)
    {
        Match match = ResetDateRegex().Match(clean);
        if (!match.Success)
        {
            return null;
        }

        string value = match.Groups["date"].Value;
        if (DateTime.TryParseExact(
            value,
            ["yyyy-M-d", "M/d/yyyy", "M/d/yy"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTime datedReset))
        {
            return new DateTimeOffset(datedReset, now.Offset);
        }

        if (!DateTime.TryParseExact(
            value,
            ["M/d", "MM/dd"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTime monthAndDay))
        {
            return null;
        }

        DateTimeOffset reset = new(
            now.Year,
            monthAndDay.Month,
            monthAndDay.Day,
            0,
            0,
            0,
            now.Offset);
        return reset.Date < now.Date ? reset.AddYears(1) : reset;
    }

    private static bool TryParseNumber(string value, out double result) =>
        double.TryParse(
            value.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out result);

    [GeneratedRegex("\\x1B\\][^\\x07]*(?:\\x07|\\x1B\\\\)")]
    private static partial Regex OscRegex();

    [GeneratedRegex("\\x1B\\[[0-9;?]*[HfABCDJK]")]
    private static partial Regex CursorCsiRegex();

    [GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex CsiRegex();

    [GeneratedRegex("\\n{2,}")]
    private static partial Regex RepeatedNewlineRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("(?:not logged in|authentication required|run [`']?kiro-cli login|(?:sign|log) in to (?:continue|kiro)|unable to authenticate)", RegexOptions.IgnoreCase)]
    private static partial Regex AuthenticationRegex();

    [GeneratedRegex("Estimated Usage\\s*\\|\\s*resets? on [^|\\r\\n]+\\|\\s*(?<plan>[^\\r\\n|]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EstimatedUsagePlanRegex();

    [GeneratedRegex("^\\s*Plan\\s*:\\s*(?<plan>[^\\r\\n]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex LabelledPlanRegex();

    [GeneratedRegex("^\\s*\\|\\s*(?<plan>(?:KIRO|Q Developer)[^|\\r\\n]+)\\|\\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex LegacyPlanRegex();

    [GeneratedRegex("(?<used>[\\d,]+(?:\\.\\d+)?)\\s+of\\s+(?<total>[\\d,]+(?:\\.\\d+)?)\\s+covered in plan", RegexOptions.IgnoreCase)]
    private static partial Regex CreditsRegex();

    [GeneratedRegex("^[^\\r\\n]*[█▓▒░■=]{3,}[^\\r\\n]*?\\s(?<value>\\d{1,3}(?:\\.\\d+)?)\\s*%", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DisplayedPercentageRegex();

    [GeneratedRegex("Bonus credits\\s*:\\s*(?<used>[\\d,]+(?:\\.\\d+)?)\\s*/\\s*(?<total>[\\d,]+(?:\\.\\d+)?)\\s+credits used(?:\\s*,?\\s*expires in\\s+(?<days>\\d+)\\s+days?)?", RegexOptions.IgnoreCase)]
    private static partial Regex BonusCreditsRegex();

    [GeneratedRegex("resets? on\\s+(?<date>\\d{4}-\\d{1,2}-\\d{1,2}|\\d{1,2}/\\d{1,2}(?:/\\d{2,4})?)", RegexOptions.IgnoreCase)]
    private static partial Regex ResetDateRegex();

    [GeneratedRegex("plan is managed by (?:an )?admin", RegexOptions.IgnoreCase)]
    private static partial Regex ManagedPlanRegex();
}
