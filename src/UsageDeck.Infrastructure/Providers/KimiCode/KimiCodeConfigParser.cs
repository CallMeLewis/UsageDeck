using System.Globalization;
using System.Text;

namespace UsageDeck.Infrastructure.Providers.KimiCode;

/// <summary>
/// Reads the string values from Kimi Code's config.toml, keyed by their full dotted path. Only
/// the subset Kimi Code writes is understood: table headers, bare, quoted and dotted keys, and
/// single-line basic or literal strings. Every other value is skipped rather than guessed at.
/// </summary>
internal static class KimiCodeConfigParser
{
    public const char PathSeparator = '\u001F';

    public static string PathKey(IEnumerable<string> segments) => string.Join(PathSeparator, segments);

    public static IReadOnlyDictionary<string, string> ParseStrings(string text)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        List<string>? table = [];
        string? multilineTerminator = null;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (multilineTerminator is not null)
            {
                if (line.Contains(multilineTerminator, StringComparison.Ordinal))
                {
                    multilineTerminator = null;
                }

                continue;
            }

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                table = ParseHeader(line);
                continue;
            }

            int index = 0;
            List<string>? key = ParseKey(line, ref index);
            if (table is null || key is null || !TrySkip(line, ref index, '='))
            {
                continue;
            }

            SkipWhitespace(line, ref index);
            string remainder = line[index..];
            if (remainder.StartsWith("\"\"\"", StringComparison.Ordinal)
                || remainder.StartsWith("'''", StringComparison.Ordinal))
            {
                string delimiter = remainder[..3];
                if (!remainder[3..].Contains(delimiter, StringComparison.Ordinal))
                {
                    multilineTerminator = delimiter;
                }

                continue;
            }

            if (index < line.Length && line[index] is '"' or '\'' && ParseString(line, ref index) is string value)
            {
                values[PathKey([.. table, .. key])] = value;
            }
        }

        return values;
    }

    private static List<string>? ParseHeader(string line)
    {
        bool isArray = line.StartsWith("[[", StringComparison.Ordinal);
        int index = isArray ? 2 : 1;
        List<string>? segments = ParseKey(line, ref index);
        if (segments is null || !TrySkip(line, ref index, ']') || (isArray && !TrySkip(line, ref index, ']')))
        {
            return null;
        }

        // Array-of-tables entries are kept apart so they cannot stand in for a plain table.
        return isArray ? ["[]", .. segments] : segments;
    }

    private static List<string>? ParseKey(string line, ref int index)
    {
        List<string> segments = [];
        while (true)
        {
            SkipWhitespace(line, ref index);
            if (index >= line.Length)
            {
                return null;
            }

            string? segment;
            if (line[index] is '"' or '\'')
            {
                segment = ParseString(line, ref index);
            }
            else
            {
                int start = index;
                while (index < line.Length && (char.IsAsciiLetterOrDigit(line[index]) || line[index] is '_' or '-'))
                {
                    index++;
                }

                segment = index > start ? line[start..index] : null;
            }

            if (segment is null)
            {
                return null;
            }

            segments.Add(segment);
            SkipWhitespace(line, ref index);
            if (index >= line.Length || line[index] != '.')
            {
                return segments;
            }

            index++;
        }
    }

    private static string? ParseString(string line, ref int index)
    {
        char quote = line[index++];
        StringBuilder value = new();
        while (index < line.Length)
        {
            char character = line[index++];
            if (character == quote)
            {
                return value.ToString();
            }

            if (quote == '\'' || character != '\\')
            {
                value.Append(character);
                continue;
            }

            if (index >= line.Length)
            {
                return null;
            }

            char escape = line[index++];
            switch (escape)
            {
                case '"': value.Append('"'); break;
                case '\\': value.Append('\\'); break;
                case 'b': value.Append('\b'); break;
                case 't': value.Append('\t'); break;
                case 'n': value.Append('\n'); break;
                case 'f': value.Append('\f'); break;
                case 'r': value.Append('\r'); break;
                case 'e': value.Append('\u001B'); break;
                case 'u' or 'U':
                    int length = escape == 'u' ? 4 : 8;
                    if (index + length > line.Length
                        || !int.TryParse(line.AsSpan(index, length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint)
                        || !Rune.IsValid(codePoint))
                    {
                        return null;
                    }

                    value.Append(new Rune(codePoint).ToString());
                    index += length;
                    break;
                default:
                    return null;
            }
        }

        return null;
    }

    private static bool TrySkip(string line, ref int index, char expected)
    {
        SkipWhitespace(line, ref index);
        if (index >= line.Length || line[index] != expected)
        {
            return false;
        }

        index++;
        return true;
    }

    private static void SkipWhitespace(string line, ref int index)
    {
        while (index < line.Length && line[index] is ' ' or '\t')
        {
            index++;
        }
    }
}
