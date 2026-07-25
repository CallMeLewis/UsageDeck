using UsageDeck.Core.Providers;

namespace UsageDeck.Infrastructure.Providers.Claude;

public sealed record ClaudeUsageDiagnosticWindow(
    string Kind,
    double UsedPercent,
    bool HasResetTime);

public sealed record ClaudeUsageDiagnostic(
    int CapturedCharacters,
    bool CaptureLimitReached,
    bool HasUsageHeading,
    bool HasCurrentSessionMarker,
    bool HasWeeklyMarker,
    bool HasCostMarker,
    bool HasSubscriptionMarker,
    IReadOnlyList<ClaudeUsageDiagnosticWindow> Windows,
    string? SafeError);

public static class ClaudeUsageDiagnostics
{
    public const int DefaultCaptureLimit = 262_144;

    public static ClaudeUsageDiagnostic Create(
        string terminalOutput,
        DateTimeOffset now,
        int captureLimit = DefaultCaptureLimit)
    {
        ArgumentNullException.ThrowIfNull(terminalOutput);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(captureLimit);

        string clean = ClaudeUsageParser.StripTerminalSequences(terminalOutput);
        IReadOnlyList<ClaudeUsageDiagnosticWindow> windows = [];
        string? safeError = null;
        try
        {
            windows = ClaudeUsageParser.Parse(terminalOutput, now)
                .Select(window => new ClaudeUsageDiagnosticWindow(
                    ClassifyWindow(window.Id),
                    window.UsedPercent,
                    window.ResetsAt is not null))
                .ToArray();
        }
        catch (Exception exception) when (exception is ProviderException or ArgumentException)
        {
            safeError = exception is ProviderException providerException
                ? providerException.SafeMessage
                : "The capture did not contain readable usage data.";
        }

        // Markers are matched against a compacted copy because a frame padded with cursor movement
        // rather than literal spaces loses all its whitespace, and a diagnostic that reports the
        // panel as absent when it is plainly there sends support down the wrong path.
        string markers = ClaudeUsageParser.Compact(clean);

        return new ClaudeUsageDiagnostic(
            Math.Min(terminalOutput.Length, captureLimit),
            terminalOutput.Length >= captureLimit,
            markers.Contains("usagelimits", StringComparison.OrdinalIgnoreCase),
            markers.Contains("currentsession", StringComparison.OrdinalIgnoreCase),
            markers.Contains("currentweek", StringComparison.OrdinalIgnoreCase),
            markers.Contains("totalcost:", StringComparison.OrdinalIgnoreCase),
            markers.Contains("currentlyusingyoursubscription", StringComparison.OrdinalIgnoreCase),
            windows,
            safeError);
    }

    private static string ClassifyWindow(string id) => id switch
    {
        "session" => "session",
        "weekly" => "weekly",
        _ when id.StartsWith("weekly-", StringComparison.Ordinal) => "weekly-model",
        _ => "other",
    };
}
