using System.Globalization;
using System.Text;
using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Settings;

namespace CodexBarWin.Infrastructure.Providers.OpenCodeGo;

public static class OpenCodeGoUsageExportParser
{
    private const int MaximumRows = 250_000;
    private const decimal MicroCentsPerDollar = 100_000_000m;
    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    private static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);
    private static readonly TimeSpan ThirtyDays = TimeSpan.FromDays(30);

    public static ProviderSnapshot Parse(
        ReadOnlySpan<byte> csv,
        DateTimeOffset capturedAt,
        OpenCodeGoUsageRange range)
    {
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(csv)
                .TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidResponse("OpenCode Console returned a usage export with invalid text encoding.", exception);
        }

        List<string[]> records = ParseCsv(text);
        if (records.Count == 0)
        {
            throw InvalidResponse("OpenCode Console returned an empty usage export.");
        }

        string[] header = records[0];
        int billingSourceIndex = FindColumn(header, "billing_source");
        int costIndex = FindColumn(header, "cost_micro_cents");
        int createdAtIndex = FindColumn(header, "created_at");
        int requiredColumns = Math.Max(billingSourceIndex, Math.Max(costIndex, createdAtIndex)) + 1;
        List<UsageRecord> usage = [];

        foreach (string[] record in records.Skip(1))
        {
            if (record.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (record.Length < requiredColumns)
            {
                throw InvalidResponse("OpenCode Console returned a malformed usage export.");
            }

            if (!string.Equals(record[billingSourceIndex].Trim(), "managed-inference", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!long.TryParse(record[costIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out long microCents)
                || microCents < 0
                || !DateTimeOffset.TryParse(
                    record[createdAtIndex],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset createdAt))
            {
                throw InvalidResponse("OpenCode Console returned invalid billing data.");
            }

            if (createdAt <= capturedAt)
            {
                usage.Add(new UsageRecord(createdAt, microCents));
            }
        }

        List<UsageWindow> windows =
        [
            CreateWindow(usage, capturedAt, "five-hour", "5-hour", FiveHours, 12m),
        ];
        if (range is OpenCodeGoUsageRange.SevenDays or OpenCodeGoUsageRange.ThirtyDays)
        {
            windows.Add(CreateWindow(usage, capturedAt, "seven-day", "7-day", SevenDays, 30m));
        }

        if (range == OpenCodeGoUsageRange.ThirtyDays)
        {
            windows.Add(CreateWindow(usage, capturedAt, "thirty-day", "30-day", ThirtyDays, 60m));
        }

        return new ProviderSnapshot(
            ProviderId.OpenCodeGo,
            ProviderId.OpenCodeGo.DisplayName,
            "OpenCode Console API billing",
            capturedAt,
            UsageDataState.Fresh,
            windows);
    }

    public static string ApiRange(OpenCodeGoUsageRange range) => range switch
    {
        OpenCodeGoUsageRange.OneDay => "24h",
        OpenCodeGoUsageRange.SevenDays => "7d",
        OpenCodeGoUsageRange.ThirtyDays => "30d",
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Unsupported OpenCode usage range."),
    };

    private static UsageWindow CreateWindow(
        IEnumerable<UsageRecord> usage,
        DateTimeOffset capturedAt,
        string id,
        string displayName,
        TimeSpan duration,
        decimal dollarLimit)
    {
        DateTimeOffset start = capturedAt.Subtract(duration);
        UsageRecord[] records = usage
            .Where(record => record.CreatedAt >= start && record.CreatedAt <= capturedAt)
            .ToArray();
        decimal totalMicroCents = records.Sum(record => (decimal)record.MicroCents);
        double usedPercent = decimal.ToDouble(decimal.Clamp(
            totalMicroCents / (dollarLimit * MicroCentsPerDollar) * 100m,
            0m,
            100m));
        DateTimeOffset resetsAt = records.Length == 0
            ? capturedAt.Add(duration)
            : records.Min(record => record.CreatedAt).Add(duration);

        return new UsageWindow(
            id,
            displayName,
            Math.Round(usedPercent, 1, MidpointRounding.AwayFromZero),
            resetsAt,
            duration,
            UsageConfidence.Estimated);
    }

    private static int FindColumn(string[] header, string name)
    {
        int index = Array.FindIndex(header, value => string.Equals(value.Trim(), name, StringComparison.OrdinalIgnoreCase));
        return index >= 0
            ? index
            : throw InvalidResponse($"OpenCode Console omitted the {name} billing field.");
    }

    private static List<string[]> ParseCsv(string text)
    {
        List<string[]> records = [];
        List<string> record = [];
        StringBuilder field = new();
        bool quoted = false;

        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }

                    AddRecord(records, record, field);
                    break;
                case '\n':
                    AddRecord(records, record, field);
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        if (quoted)
        {
            throw InvalidResponse("OpenCode Console returned a malformed usage export.");
        }

        if (field.Length > 0 || record.Count > 0)
        {
            AddRecord(records, record, field);
        }

        return records;
    }

    private static void AddRecord(List<string[]> records, List<string> record, StringBuilder field)
    {
        record.Add(field.ToString());
        field.Clear();
        records.Add(record.ToArray());
        record.Clear();
        if (records.Count > MaximumRows + 1)
        {
            throw InvalidResponse("OpenCode Console returned too many usage records to process safely.");
        }
    }

    private static ProviderException InvalidResponse(string message, Exception? innerException = null) =>
        innerException is null
            ? new ProviderException(ProviderErrorCategory.InvalidResponse, message)
            : new ProviderException(ProviderErrorCategory.InvalidResponse, message, innerException);

    private sealed record UsageRecord(DateTimeOffset CreatedAt, long MicroCents);
}
