using CodexBarWin.Core.Providers;
using Microsoft.Data.Sqlite;

namespace CodexBarWin.Infrastructure.Providers.OpenCodeGo;

public interface IOpenCodeGoUsageReader
{
    ProviderSnapshot Read(string databasePath, DateTimeOffset capturedAt);
}

public sealed class OpenCodeGoUsageReader : IOpenCodeGoUsageReader
{
    private const int MaximumRows = 250_000;
    private static readonly TimeSpan SessionDuration = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeekDuration = TimeSpan.FromDays(7);

    public ProviderSnapshot Read(string databasePath, DateTimeOffset capturedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new ProviderException(
                ProviderErrorCategory.NotInstalled,
                "OpenCode Go local history was not found. Use OpenCode Go once, then refresh.");
        }

        SqliteConnectionStringBuilder connectionString = new()
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1,
        };

        using SqliteConnection connection = new(connectionString.ToString());
        connection.Open();
        List<UsageRow> rows = ReadRows(connection);
        if (rows.Count == 0)
        {
            return new ProviderSnapshot(
                ProviderId.OpenCodeGo,
                ProviderId.OpenCodeGo.DisplayName,
                "Local OpenCode history",
                capturedAt,
                UsageDataState.Fresh);
        }

        return CreateSnapshot(rows, capturedAt);
    }

    private static List<UsageRow> ReadRows(SqliteConnection connection)
    {
        if (!HasTable(connection, "message") || !HasColumn(connection, "message", "data"))
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "OpenCode Go local history uses an unsupported database format.");
        }

        bool messageHasTimeCreated = HasColumn(connection, "message", "time_created");
        bool messageHasId = HasColumn(connection, "message", "id");
        bool hasPartTable = HasTable(connection, "part")
            && HasColumn(connection, "part", "data")
            && HasColumn(connection, "part", "message_id")
            && messageHasId;
        bool partHasTimeCreated = hasPartTable && HasColumn(connection, "part", "time_created");

        string messageTime = messageHasTimeCreated
            ? "COALESCE(json_extract(data, '$.time.created'), time_created)"
            : "json_extract(data, '$.time.created')";
        string messageCosts = $"""
            SELECT
              {(messageHasId ? "id" : "NULL")} AS messageID,
              CAST({messageTime} AS INTEGER) AS createdMs,
              CAST(json_extract(data, '$.cost') AS REAL) AS cost
            FROM message
            WHERE json_valid(data)
              AND json_extract(data, '$.providerID') = 'opencode-go'
              AND json_extract(data, '$.role') = 'assistant'
              AND json_type(data, '$.cost') IN ('integer', 'real')
            """;

        string sql;
        if (hasPartTable)
        {
            string partTime = partHasTimeCreated
                ? messageHasTimeCreated
                    ? "COALESCE(json_extract(p.data, '$.time.created'), p.time_created, m.time_created)"
                    : "COALESCE(json_extract(p.data, '$.time.created'), p.time_created)"
                : messageHasTimeCreated
                    ? "COALESCE(json_extract(p.data, '$.time.created'), m.time_created)"
                    : "json_extract(p.data, '$.time.created')";
            sql = $"""
                WITH message_costs AS (
                  {messageCosts}
                )
                SELECT createdMs, cost FROM message_costs
                UNION ALL
                SELECT
                  CAST({partTime} AS INTEGER) AS createdMs,
                  CAST(json_extract(p.data, '$.cost') AS REAL) AS cost
                FROM part p
                JOIN message m ON m.id = p.message_id
                WHERE json_valid(p.data)
                  AND json_valid(m.data)
                  AND json_extract(p.data, '$.type') = 'step-finish'
                  AND json_type(p.data, '$.cost') IN ('integer', 'real')
                  AND json_extract(m.data, '$.providerID') = 'opencode-go'
                  AND json_extract(m.data, '$.role') = 'assistant'
                  AND NOT EXISTS (
                    SELECT 1 FROM message_costs WHERE message_costs.messageID = p.message_id
                  )
                """;
        }
        else
        {
            sql = $"SELECT createdMs, cost FROM ({messageCosts})";
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 1;
        using SqliteDataReader reader = command.ExecuteReader();
        List<UsageRow> rows = [];
        while (reader.Read())
        {
            if (rows.Count >= MaximumRows)
            {
                throw new ProviderException(
                    ProviderErrorCategory.InvalidResponse,
                    "OpenCode Go local history is too large to process safely.");
            }

            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }

            long createdMs = reader.GetInt64(0);
            double cost = reader.GetDouble(1);
            if (createdMs > 0 && double.IsFinite(cost) && cost >= 0)
            {
                rows.Add(new UsageRow(createdMs, cost));
            }
        }

        return rows;
    }

    private static bool HasTable(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ProviderSnapshot CreateSnapshot(List<UsageRow> rows, DateTimeOffset capturedAt)
    {
        DateTimeOffset now = capturedAt.ToUniversalTime();
        long nowMs = now.ToUnixTimeMilliseconds();
        long sessionStartMs = now.Subtract(SessionDuration).ToUnixTimeMilliseconds();
        DateTimeOffset weekStart = StartOfUtcWeek(now);
        DateTimeOffset weekEnd = weekStart.Add(WeekDuration);
        DateTimeOffset earliest = DateTimeOffset.FromUnixTimeMilliseconds(rows.Min(row => row.CreatedMs));
        (DateTimeOffset MonthStart, DateTimeOffset MonthEnd) month = MonthBounds(now, earliest);

        double sessionCost = Sum(rows, sessionStartMs, nowMs);
        double weeklyCost = Sum(rows, weekStart.ToUnixTimeMilliseconds(), weekEnd.ToUnixTimeMilliseconds());
        double monthlyCost = Sum(rows, month.MonthStart.ToUnixTimeMilliseconds(), month.MonthEnd.ToUnixTimeMilliseconds());
        DateTimeOffset sessionReset = rows
            .Where(row => row.CreatedMs >= sessionStartMs && row.CreatedMs < nowMs)
            .Select(row => DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedMs).Add(SessionDuration))
            .DefaultIfEmpty(now.Add(SessionDuration))
            .Min();

        UsageWindow[] windows =
        [
            new(
                "five-hour",
                "5-hour",
                Percent(sessionCost, 12),
                sessionReset,
                SessionDuration,
                UsageConfidence.Estimated),
            new(
                "weekly",
                "Weekly",
                Percent(weeklyCost, 30),
                weekEnd,
                WeekDuration,
                UsageConfidence.Estimated),
            new(
                "monthly",
                "Monthly",
                Percent(monthlyCost, 60),
                month.MonthEnd,
                month.MonthEnd - month.MonthStart,
                UsageConfidence.Estimated),
        ];

        return new ProviderSnapshot(
            ProviderId.OpenCodeGo,
            ProviderId.OpenCodeGo.DisplayName,
            "Local OpenCode history",
            capturedAt,
            UsageDataState.Fresh,
            windows);
    }

    private static double Sum(List<UsageRow> rows, long startMs, long endMs) =>
        rows.Where(row => row.CreatedMs >= startMs && row.CreatedMs < endMs).Sum(row => row.Cost);

    private static double Percent(double used, double limit) =>
        Math.Round(Math.Clamp(used / limit * 100, 0, 100), 1, MidpointRounding.AwayFromZero);

    private static DateTimeOffset StartOfUtcWeek(DateTimeOffset now)
    {
        int daysSinceMonday = ((int)now.DayOfWeek + 6) % 7;
        return new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero)
            .AddDays(-daysSinceMonday);
    }

    private static (DateTimeOffset MonthStart, DateTimeOffset MonthEnd) MonthBounds(
        DateTimeOffset now,
        DateTimeOffset anchor)
    {
        DateTimeOffset monthStart = AnchoredMonth(now.Year, now.Month, anchor);
        if (monthStart > now)
        {
            DateTimeOffset previousMonth = now.AddMonths(-1);
            monthStart = AnchoredMonth(previousMonth.Year, previousMonth.Month, anchor);
        }

        DateTimeOffset nextMonth = monthStart.AddMonths(1);
        DateTimeOffset monthEnd = AnchoredMonth(nextMonth.Year, nextMonth.Month, anchor);
        return (monthStart, monthEnd);
    }

    private static DateTimeOffset AnchoredMonth(int year, int month, DateTimeOffset anchor)
    {
        int day = Math.Min(anchor.Day, DateTime.DaysInMonth(year, month));
        return new DateTimeOffset(
            year,
            month,
            day,
            anchor.Hour,
            anchor.Minute,
            anchor.Second,
            anchor.Millisecond,
            TimeSpan.Zero);
    }

    private sealed record UsageRow(long CreatedMs, double Cost);
}
