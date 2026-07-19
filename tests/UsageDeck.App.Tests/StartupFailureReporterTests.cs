namespace UsageDeck.App.Tests;

public sealed class StartupFailureReporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "UsageDeck.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReportContainsActionableDetailsWithoutExceptionData()
    {
        InvalidOperationException exception = new(
            $"Could not open {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\UsageDeck.",
            new IOException("The XAML resource was unavailable."));
        exception.Data["credential"] = "must-not-be-written";

        string report = StartupFailureReporter.CreateReport(
            "Opening the Windows interface",
            exception,
            new DateTimeOffset(2026, 7, 19, 18, 30, 0, TimeSpan.Zero));

        Assert.Contains("UTC: 2026-07-19T18:30:00.0000000Z", report, StringComparison.Ordinal);
        Assert.Contains("Stage: Opening the Windows interface", report, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", report, StringComparison.Ordinal);
        Assert.Contains("HRESULT: 0x80131509", report, StringComparison.Ordinal);
        Assert.Contains("%LOCALAPPDATA%", report, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException", report, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-be-written", report, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            report,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryWriteCreatesAReportAtTheRequestedPath()
    {
        string path = Path.Combine(this._directory, "diagnostics", "startup-failure.log");

        string? writtenPath = StartupFailureReporter.TryWrite(
            "Starting the Windows interface",
            new InvalidOperationException("Invalid state."),
            path,
            new DateTimeOffset(2026, 7, 19, 18, 30, 0, TimeSpan.Zero));

        Assert.Equal(path, writtenPath);
        Assert.True(File.Exists(path));
        Assert.Contains("Invalid state.", File.ReadAllText(path), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(this._directory))
        {
            Directory.Delete(this._directory, recursive: true);
        }
    }
}
