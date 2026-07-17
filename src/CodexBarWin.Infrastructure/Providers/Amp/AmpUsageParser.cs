using System.Globalization;
using System.Text.RegularExpressions;
using CodexBarWin.Core.Providers;

namespace CodexBarWin.Infrastructure.Providers.Amp;

public static partial class AmpUsageParser
{
    public static ProviderSnapshot Parse(string output, DateTimeOffset capturedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(output);

        string clean = StripTerminalSequences(output);
        Match identityMatch = IdentityRegex().Match(clean);
        if (!identityMatch.Success && SignedOutRegex().IsMatch(clean))
        {
            throw new ProviderException(
                ProviderErrorCategory.AuthenticationRequired,
                "Amp needs you to sign in. Run `amp login`, then refresh.");
        }

        List<UsageWindow> windows = [];
        Match freeBalance = FreeBalanceRegex().Match(clean);
        if (freeBalance.Success
            && TryParseNumber(freeBalance.Groups["remaining"].Value, out double remaining)
            && TryParseNumber(freeBalance.Groups["quota"].Value, out double quota)
            && quota > 0)
        {
            double used = Math.Max(0, quota - remaining);
            double replenishment = TryParseNumber(
                freeBalance.Groups["replenishment"].Value,
                out double parsedReplenishment)
                    ? parsedReplenishment
                    : 0;
            TimeSpan? duration = replenishment > 0
                ? TimeSpan.FromHours(Math.Max(1, Math.Round(quota / replenishment)))
                : null;
            DateTimeOffset? resetsAt = replenishment > 0
                ? capturedAt.AddHours(used / replenishment)
                : null;
            windows.Add(new UsageWindow(
                "amp-free",
                "Amp Free",
                used / quota * 100,
                resetsAt,
                duration,
                UsageConfidence.Parsed));
        }
        else
        {
            Match percentage = FreePercentageRegex().Match(clean);
            if (percentage.Success
                && TryParseNumber(percentage.Groups["remaining"].Value, out double remainingPercent))
            {
                windows.Add(new UsageWindow(
                    "amp-free",
                    "Amp Free",
                    100 - Math.Clamp(remainingPercent, 0, 100),
                    duration: TimeSpan.FromHours(24),
                    confidence: UsageConfidence.Parsed));
            }
        }

        double? individualCredits = null;
        Match individual = IndividualCreditsRegex().Match(clean);
        if (individual.Success
            && TryParseNumber(individual.Groups["remaining"].Value, out double individualRemaining))
        {
            individualCredits = individualRemaining;
        }

        List<(string Name, double Remaining)> workspaceCredits = [];
        foreach (Match workspace in WorkspaceCreditsRegex().Matches(clean))
        {
            string name = workspace.Groups["name"].Value.Trim();
            if (name.Length is > 0 and <= 100
                && TryParseNumber(workspace.Groups["remaining"].Value, out double workspaceRemaining))
            {
                workspaceCredits.Add((name, workspaceRemaining));
            }
        }

        CreditBalance? credits = BuildCreditBalance(individualCredits, workspaceCredits);
        if (windows.Count == 0 && credits is null)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Amp opened, but its usage details could not be read.");
        }

        string? email = identityMatch.Success ? NonEmpty(identityMatch.Groups["email"].Value) : null;
        string? organisation = identityMatch.Success ? NonEmpty(identityMatch.Groups["organisation"].Value) : null;
        return new ProviderSnapshot(
            ProviderId.Amp,
            ProviderId.Amp.DisplayName,
            "Amp CLI",
            capturedAt,
            UsageDataState.Fresh,
            windows,
            new AccountIdentity(email, windows.Count > 0 ? "Amp Free" : null, organisation),
            credits);
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

    private static CreditBalance? BuildCreditBalance(
        double? individualCredits,
        List<(string Name, double Remaining)> workspaceCredits)
    {
        if (individualCredits is null && workspaceCredits.Count == 0)
        {
            return null;
        }

        List<string> balances = [];
        if (individualCredits is not null)
        {
            balances.Add($"Individual: {FormatCurrency(individualCredits.Value)}");
        }

        balances.AddRange(workspaceCredits.Select(workspace =>
            $"Workspace {workspace.Name}: {FormatCurrency(workspace.Remaining)}"));
        return new CreditBalance(string.Join(" · ", balances), HasCredits: true, IsUnlimited: false);
    }

    private static string FormatCurrency(double value) =>
        value.ToString("$#,##0.##", CultureInfo.InvariantCulture);

    private static string? NonEmpty(string value)
    {
        string clean = value.Trim();
        return clean.Length == 0 ? null : clean;
    }

    private static bool TryParseNumber(string value, out double result) =>
        double.TryParse(
            value.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out result)
        && double.IsFinite(result)
        && result >= 0;

    [GeneratedRegex("\\x1B\\][^\\x07]*(?:\\x07|\\x1B\\\\)")]
    private static partial Regex OscRegex();

    [GeneratedRegex("\\x1B\\[[0-9;?]*[HfABCDJK]")]
    private static partial Regex CursorCsiRegex();

    [GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex CsiRegex();

    [GeneratedRegex("\\n{2,}")]
    private static partial Regex RepeatedNewlineRegex();

    [GeneratedRegex("^\\s*Signed in as\\s+(?<email>[^\\s(]+)(?:\\s+\\((?<organisation>[^\\r\\n)]+)\\))?\\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex IdentityRegex();

    [GeneratedRegex("(?:please\\s+)?(?:sign|log)\\s+in|not logged in|authentication required|ampcode\\.com/(?:auth/)?(?:sign-in|login)", RegexOptions.IgnoreCase)]
    private static partial Regex SignedOutRegex();

    [GeneratedRegex("^\\s*Amp Free:\\s*\\$?(?<remaining>[0-9][0-9,]*(?:\\.[0-9]+)?)\\s*/\\s*\\$?(?<quota>[0-9][0-9,]*(?:\\.[0-9]+)?)\\s+remaining(?:\\s*\\(replenishes\\s*\\+\\$?(?<replenishment>[0-9][0-9,]*(?:\\.[0-9]+)?)\\s*/\\s*hour\\))?", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex FreeBalanceRegex();

    [GeneratedRegex("^\\s*Amp Free:\\s*(?<remaining>[0-9][0-9,]*(?:\\.[0-9]+)?)\\s*%\\s+remaining(?:\\s+today)?(?:\\s*\\(resets\\s+daily\\))?", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex FreePercentageRegex();

    [GeneratedRegex("^\\s*Individual credits:\\s*\\$?(?<remaining>[0-9][0-9,]*(?:\\.[0-9]+)?)\\s+remaining", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex IndividualCreditsRegex();

    [GeneratedRegex("^\\s*Workspace\\s+(?<name>.+?):\\s*\\$?(?<remaining>[0-9][0-9,]*(?:\\.[0-9]+)?)\\s+remaining", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WorkspaceCreditsRegex();
}
