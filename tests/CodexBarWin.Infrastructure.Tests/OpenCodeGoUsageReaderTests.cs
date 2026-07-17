using System.Globalization;
using System.Text.Json;
using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Providers.OpenCodeGo;
using Microsoft.Data.Sqlite;

namespace CodexBarWin.Infrastructure.Tests;

public sealed class OpenCodeGoUsageReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "CodexBarWin.OpenCodeGo.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReadsLocalCostsIntoEstimatedPlanWindows()
    {
        string databasePath = this.CreateDatabase();
        InsertMessage(databasePath, Timestamp("2026-03-06T11:00:00.000Z"), 3);
        InsertMessage(databasePath, Timestamp("2026-03-05T12:00:00.000Z"), 6);
        InsertMessage(databasePath, Timestamp("2026-02-25T07:53:16.000Z"), 2);
        DateTimeOffset now = ParseTime("2026-03-06T12:00:00.000Z");

        ProviderSnapshot snapshot = new OpenCodeGoUsageReader().Read(databasePath, now);

        Assert.Equal(ProviderId.OpenCodeGo, snapshot.ProviderId);
        Assert.Equal("Local OpenCode history", snapshot.SourceDescription);
        Assert.Null(snapshot.Identity);
        Assert.Collection(
            snapshot.UsageWindows,
            window => AssertWindow(window, "5-hour", 25, now.AddHours(4)),
            window => AssertWindow(window, "Weekly", 30, ParseTime("2026-03-09T00:00:00.000Z")),
            window => AssertWindow(window, "Monthly", 18.3, ParseTime("2026-03-25T07:53:16.000Z")));
        Assert.All(snapshot.UsageWindows, window => Assert.Equal(UsageConfidence.Estimated, window.Confidence));
    }

    [Fact]
    public void UsesStepFinishCostsOnlyWhenTheMessageHasNoCost()
    {
        string databasePath = this.CreateDatabase();
        long createdMs = Timestamp("2027-01-15T07:59:00.000Z");
        string directMessage = InsertMessage(databasePath, createdMs, 3);
        InsertPart(databasePath, directMessage, createdMs, 3);
        string metadataMessage = InsertMessage(databasePath, createdMs, null);
        InsertPart(databasePath, metadataMessage, createdMs, 3);

        ProviderSnapshot snapshot = new OpenCodeGoUsageReader().Read(
            databasePath,
            ParseTime("2027-01-15T08:00:00.000Z"));

        Assert.Equal(50, snapshot.UsageWindows[0].UsedPercent);
        Assert.Equal(20, snapshot.UsageWindows[1].UsedPercent);
        Assert.Equal(10, snapshot.UsageWindows[2].UsedPercent);
    }

    [Fact]
    public void EmptyOpenCodeGoHistoryReturnsACleanEmptySnapshot()
    {
        string databasePath = this.CreateDatabase();
        DateTimeOffset now = ParseTime("2027-01-15T08:00:00.000Z");

        ProviderSnapshot snapshot = new OpenCodeGoUsageReader().Read(databasePath, now);

        Assert.Equal(UsageDataState.Fresh, snapshot.State);
        Assert.Equal("Local OpenCode history", snapshot.SourceDescription);
        Assert.Null(snapshot.Identity);
        Assert.Empty(snapshot.UsageWindows);
        Assert.Null(snapshot.SafeError);
    }

    [Fact]
    public void UnsupportedDatabaseReturnsOnlyASafeError()
    {
        Directory.CreateDirectory(this._directory);
        string databasePath = Path.Combine(this._directory, "opencode.db");
        using (SqliteConnection connection = new($"Data Source={databasePath}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (value TEXT)";
            command.ExecuteNonQuery();
        }

        ProviderException exception = Assert.Throws<ProviderException>(() =>
            new OpenCodeGoUsageReader().Read(databasePath, DateTimeOffset.UtcNow));

        Assert.Equal(ProviderErrorCategory.InvalidResponse, exception.Category);
        Assert.DoesNotContain(databasePath, exception.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadsDirectCostsFromAMinimalMessageSchema()
    {
        Directory.CreateDirectory(this._directory);
        string databasePath = Path.Combine(this._directory, "opencode.db");
        long createdMs = Timestamp("2027-01-15T07:59:00.000Z");
        using (SqliteConnection connection = new($"Data Source={databasePath}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE message (data TEXT NOT NULL)";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO message (data) VALUES ($data)";
            command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(new
            {
                providerID = "opencode-go",
                role = "assistant",
                cost = 3,
                time = new { created = createdMs },
            }));
            command.ExecuteNonQuery();
        }

        ProviderSnapshot snapshot = new OpenCodeGoUsageReader().Read(
            databasePath,
            ParseTime("2027-01-15T08:00:00.000Z"));

        Assert.Equal(25, snapshot.UsageWindows[0].UsedPercent);
    }

    [Fact]
    public void DataLocatorPrefersXdgThenWindowsAndLegacyLocations()
    {
        string xdg = Path.Combine(this._directory, "xdg");
        string local = Path.Combine(this._directory, "local");
        string profile = Path.Combine(this._directory, "profile");
        string xdgDatabase = Path.Combine(xdg, "opencode", "opencode.db");
        Directory.CreateDirectory(Path.GetDirectoryName(xdgDatabase)!);
        File.WriteAllBytes(xdgDatabase, []);
        OpenCodeGoDataLocator locator = new(
            local,
            profile,
            name => name == "XDG_DATA_HOME" ? xdg : null);

        Assert.Equal(Path.GetFullPath(xdgDatabase), locator.FindDatabasePath());
        Assert.Equal(
            [
                Path.GetFullPath(xdgDatabase),
                Path.GetFullPath(Path.Combine(local, "opencode", "opencode.db")),
                Path.GetFullPath(Path.Combine(profile, ".local", "share", "opencode", "opencode.db")),
            ],
            locator.GetCandidateDatabasePaths());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(this._directory))
        {
            Directory.Delete(this._directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static void AssertWindow(
        UsageWindow window,
        string displayName,
        double usedPercent,
        DateTimeOffset reset)
    {
        Assert.Equal(displayName, window.DisplayName);
        Assert.Equal(usedPercent, window.UsedPercent);
        Assert.Equal(reset, window.ResetsAt);
    }

    private string CreateDatabase()
    {
        Directory.CreateDirectory(this._directory);
        string databasePath = Path.Combine(this._directory, "opencode.db");
        using SqliteConnection connection = new($"Data Source={databasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE message (
              id TEXT PRIMARY KEY,
              session_id TEXT NOT NULL,
              data TEXT NOT NULL,
              time_created INTEGER,
              time_updated INTEGER
            );
            CREATE TABLE part (
              id TEXT PRIMARY KEY,
              message_id TEXT NOT NULL,
              session_id TEXT NOT NULL,
              data TEXT NOT NULL,
              time_created INTEGER,
              time_updated INTEGER
            );
            """;
        command.ExecuteNonQuery();
        return databasePath;
    }

    private static string InsertMessage(string databasePath, long createdMs, double? cost)
    {
        string messageId = Guid.NewGuid().ToString("N");
        Dictionary<string, object> payload = new()
        {
            ["providerID"] = "opencode-go",
            ["role"] = "assistant",
            ["time"] = new Dictionary<string, long> { ["created"] = createdMs },
        };
        if (cost is not null)
        {
            payload["cost"] = cost.Value;
        }

        using SqliteConnection connection = new($"Data Source={databasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO message (id, session_id, data, time_created, time_updated)
            VALUES ($id, 'session-1', $data, $created, $created)
            """;
        command.Parameters.AddWithValue("$id", messageId);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(payload));
        command.Parameters.AddWithValue("$created", createdMs);
        command.ExecuteNonQuery();
        return messageId;
    }

    private static void InsertPart(string databasePath, string messageId, long createdMs, double cost)
    {
        string payload = JsonSerializer.Serialize(new
        {
            type = "step-finish",
            cost,
        });
        using SqliteConnection connection = new($"Data Source={databasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO part (id, message_id, session_id, data, time_created, time_updated)
            VALUES ($id, $message, 'session-1', $data, $created, $created)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$message", messageId);
        command.Parameters.AddWithValue("$data", payload);
        command.Parameters.AddWithValue("$created", createdMs);
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static long Timestamp(string value) => ParseTime(value).ToUnixTimeMilliseconds();
}
