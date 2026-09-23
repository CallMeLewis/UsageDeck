using System.Globalization;
using System.Text;

namespace UsageDeck.Infrastructure.Processes;

/// <summary>
/// Replays captured terminal output onto a fixed-size grid so callers can read what ended up on
/// screen. A pseudo-terminal repaints only the cells that changed, so text updated in place never
/// appears in the raw stream as readable words: "used" over "uses" arrives as a single "d".
/// Rows that scroll off the top are kept, in order, ahead of the visible rows.
/// </summary>
public static class TerminalScreen
{
    private const char Escape = '\u001b';

    public static string Render(string output, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);

        Grid grid = new(columns, rows);
        int index = 0;
        while (index < output.Length)
        {
            char current = output[index];
            if (current == Escape)
            {
                index = ApplyEscape(output, index, grid);
                continue;
            }

            switch (current)
            {
                case '\r':
                    grid.Column = 0;
                    break;
                case '\n':
                    // A pseudo-terminal always pairs the two, and captures written by hand for
                    // tests rarely do, so a bare line feed starts the next line too.
                    grid.Column = 0;
                    grid.LineFeed();
                    break;
                case '\b':
                    grid.Column = Math.Max(0, grid.Column - 1);
                    break;
                default:
                    if (!char.IsControl(current))
                    {
                        bool isPair = char.IsHighSurrogate(current)
                            && index + 1 < output.Length
                            && char.IsLowSurrogate(output[index + 1]);
                        grid.Write(isPair ? output.Substring(index, 2) : current.ToString());
                        index += isPair ? 1 : 0;
                    }

                    break;
            }

            index++;
        }

        return grid.ToText();
    }

    private static int ApplyEscape(string output, int start, Grid grid)
    {
        if (start + 1 >= output.Length)
        {
            return output.Length;
        }

        char kind = output[start + 1];
        switch (kind)
        {
            case '[':
                return ApplyControlSequence(output, start + 2, grid);
            case ']':
                return SkipOperatingSystemCommand(output, start + 2);
            case '7':
                grid.SaveCursor();
                return start + 2;
            case '8':
                grid.RestoreCursor();
                return start + 2;
            case '(' or ')' or '*' or '+':
                // Character set selection carries one more byte.
                return Math.Min(output.Length, start + 3);
            default:
                return start + 2;
        }
    }

    private static int SkipOperatingSystemCommand(string output, int index)
    {
        while (index < output.Length)
        {
            if (output[index] == '\u0007')
            {
                return index + 1;
            }

            if (output[index] == Escape && index + 1 < output.Length && output[index + 1] == '\\')
            {
                return index + 2;
            }

            index++;
        }

        return index;
    }

    private static int ApplyControlSequence(string output, int index, Grid grid)
    {
        int parametersStart = index;
        while (index < output.Length && output[index] is >= '0' and <= '?')
        {
            index++;
        }

        string parameters = output[parametersStart..index];
        while (index < output.Length && output[index] is >= ' ' and <= '/')
        {
            index++;
        }

        if (index >= output.Length)
        {
            return index;
        }

        char command = output[index];
        index++;

        // Private sequences such as "?25l" change modes, not the text on screen.
        if (parameters.Length > 0 && parameters[0] is '?' or '>' or '<' or '=')
        {
            return index;
        }

        int[] values = ParseParameters(parameters);
        int first = values.Length > 0 ? values[0] : 0;
        int count = Math.Max(1, first);
        switch (command)
        {
            case 'A':
                grid.Row = Math.Max(0, grid.Row - count);
                break;
            case 'B':
                grid.Row = Math.Min(grid.Rows - 1, grid.Row + count);
                break;
            case 'C':
                grid.Column = Math.Min(grid.Columns - 1, grid.Column + count);
                break;
            case 'D':
                grid.Column = Math.Max(0, grid.Column - count);
                break;
            case 'E':
                grid.Row = Math.Min(grid.Rows - 1, grid.Row + count);
                grid.Column = 0;
                break;
            case 'F':
                grid.Row = Math.Max(0, grid.Row - count);
                grid.Column = 0;
                break;
            case 'G':
                grid.Column = Math.Clamp(count - 1, 0, grid.Columns - 1);
                break;
            case 'd':
                grid.Row = Math.Clamp(count - 1, 0, grid.Rows - 1);
                break;
            case 'H' or 'f':
                grid.Row = Math.Clamp(count - 1, 0, grid.Rows - 1);
                grid.Column = Math.Clamp((values.Length > 1 ? Math.Max(1, values[1]) : 1) - 1, 0, grid.Columns - 1);
                break;
            case 'J':
                grid.EraseDisplay(first);
                break;
            case 'K':
                grid.EraseLine(first);
                break;
            case 'X':
                grid.EraseCharacters(count);
                break;
            default:
                break;
        }

        return index;
    }

    private static int[] ParseParameters(string parameters) =>
        parameters.Length == 0
            ? []
            : parameters
                .Split(';')
                .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0)
                .ToArray();

    private sealed class Grid(int columns, int rows)
    {
        private readonly List<string?[]> _scrolledOff = [];
        private readonly List<string?[]> _visible = Enumerable.Range(0, rows).Select(_ => new string?[columns]).ToList();
        private int _savedRow;
        private int _savedColumn;

        public int Columns => columns;

        public int Rows => rows;

        public int Row { get; set; }

        public int Column { get; set; }

        public void Write(string cell)
        {
            if (this.Column >= columns)
            {
                this.Column = 0;
                this.LineFeed();
            }

            this._visible[this.Row][this.Column] = cell;
            this.Column++;
        }

        public void LineFeed()
        {
            if (this.Row < rows - 1)
            {
                this.Row++;
                return;
            }

            this._scrolledOff.Add(this._visible[0]);
            this._visible.RemoveAt(0);
            this._visible.Add(new string?[columns]);
        }

        public void SaveCursor()
        {
            this._savedRow = this.Row;
            this._savedColumn = this.Column;
        }

        public void RestoreCursor()
        {
            this.Row = this._savedRow;
            this.Column = this._savedColumn;
        }

        public void EraseLine(int mode)
        {
            int column = Math.Min(this.Column, columns - 1);
            (int from, int to) = mode switch
            {
                1 => (0, column + 1),
                2 => (0, columns),
                _ => (column, columns),
            };
            Array.Clear(this._visible[this.Row], from, to - from);
        }

        public void EraseCharacters(int count)
        {
            int column = Math.Min(this.Column, columns - 1);
            Array.Clear(this._visible[this.Row], column, Math.Min(count, columns - column));
        }

        public void EraseDisplay(int mode)
        {
            if (mode is 2 or 3)
            {
                foreach (string?[] line in this._visible)
                {
                    Array.Clear(line);
                }

                return;
            }

            this.EraseLine(mode);
            int from = mode == 1 ? 0 : this.Row + 1;
            int to = mode == 1 ? this.Row : rows;
            for (int row = from; row < to; row++)
            {
                Array.Clear(this._visible[row]);
            }
        }

        public string ToText()
        {
            StringBuilder text = new();
            foreach (string?[] line in this._scrolledOff.Concat(this._visible))
            {
                StringBuilder rendered = new(columns);
                foreach (string? cell in line)
                {
                    rendered.Append(cell ?? " ");
                }

                text.Append(rendered.ToString().TrimEnd()).Append('\n');
            }

            return text.ToString().TrimEnd('\n');
        }
    }
}
