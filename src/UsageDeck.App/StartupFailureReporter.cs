using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security;
using System.Text;
using UsageDeck.Infrastructure.Compatibility;

namespace UsageDeck.App;

internal static class StartupFailureReporter
{
    private const int MaximumExceptionDepth = 4;
    private const int MaximumMessageLength = 4_096;
    private const int MaximumStackTraceLength = 32_768;
    private const string ReportFileName = "startup-failure.log";

    private static int _dialogShown;

    public static string DiagnosticPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationIdentity.LocalDataDirectoryName,
        "diagnostics",
        ReportFileName);

    public static void InstallLastChanceHandler()
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            _ = sender;
            if (args.ExceptionObject is Exception exception)
            {
                _ = TryWrite("Unhandled .NET startup exception", exception);
            }
        };
    }

    [DoesNotReturn]
    public static void ReportAndExit(string stage, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(exception);

        string? diagnosticPath = TryWrite(stage, exception);
        if (Interlocked.Exchange(ref _dialogShown, 1) == 0)
        {
            StartupFailureDialog.Show(exception.HResult, diagnosticPath);
        }

        Environment.Exit(1);
        throw new InvalidOperationException("Environment.Exit returned unexpectedly.");
    }

    internal static string? TryWrite(
        string stage,
        Exception exception,
        string? diagnosticPath = null,
        DateTimeOffset? timestamp = null)
    {
        try
        {
            string path = diagnosticPath ?? DiagnosticPath;
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            Directory.CreateDirectory(directory);
            string report = CreateReport(stage, exception, timestamp ?? DateTimeOffset.UtcNow);
            File.WriteAllText(path, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch (Exception writeException) when (writeException is
            ArgumentException
            or IOException
            or NotSupportedException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string CreateReport(
        string stage,
        Exception exception,
        DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(exception);

        (string Value, string Replacement)[] redactions = CreateRedactions();
        StringBuilder report = new();
        _ = report.AppendLine("UsageDeck startup failure")
            .Append("UTC: ").AppendLine(timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
            .Append("Version: ").AppendLine(BuildInformation.Version)
            .Append("Stage: ").AppendLine(Sanitise(stage, MaximumMessageLength, redactions));

        Exception? current = exception;
        for (int depth = 0; current is not null && depth < MaximumExceptionDepth; depth++)
        {
            _ = report.AppendLine()
                .Append("Exception ").Append(depth + 1).AppendLine(":")
                .Append("Type: ").AppendLine(current.GetType().FullName ?? current.GetType().Name)
                .Append("HRESULT: 0x").AppendLine(current.HResult.ToString("X8", CultureInfo.InvariantCulture))
                .Append("Message: ").AppendLine(Sanitise(current.Message, MaximumMessageLength, redactions));

            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                _ = report.AppendLine("Stack trace:")
                    .AppendLine(Sanitise(current.StackTrace, MaximumStackTraceLength, redactions));
            }

            current = current.InnerException;
        }

        if (current is not null)
        {
            _ = report.AppendLine().AppendLine("Additional inner exceptions were omitted.");
        }

        return report.ToString();
    }

    private static (string Value, string Replacement)[] CreateRedactions() =>
    [
        (AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), "%APPDIR%"),
        (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
        (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
    ];

    private static string Sanitise(
        string? value,
        int maximumLength,
        IEnumerable<(string Value, string Replacement)> redactions)
    {
        string sanitised = string.IsNullOrWhiteSpace(value)
            ? "(not provided)"
            : value.Replace("\0", string.Empty, StringComparison.Ordinal);
        foreach ((string path, string replacement) in redactions
            .Where(redaction => !string.IsNullOrWhiteSpace(redaction.Value))
            .OrderByDescending(redaction => redaction.Value.Length))
        {
            sanitised = sanitised.Replace(path, replacement, StringComparison.OrdinalIgnoreCase);
        }

        return sanitised.Length <= maximumLength
            ? sanitised
            : sanitised[..maximumLength] + "… [truncated]";
    }
}
