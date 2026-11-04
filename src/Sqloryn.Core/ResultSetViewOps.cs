using System.Data.SqlTypes;
using System.Globalization;

namespace Sqloryn.Core;

/// <summary>
/// Sort and filter over the rows already retained in memory. Neither operation re-queries the server, so on a
/// truncated set they only reorder or narrow the retained prefix; the derived view stays marked
/// <see cref="ResultSetView.Truncated"/> and carries a <see cref="ScopeLabel"/> saying so.
/// </summary>
public sealed partial class ResultSetView
{
    /// <summary>
    /// Set on views returned by <see cref="SortByColumn"/> / <see cref="FilterContains"/>; null on source views.
    /// </summary>
    public string? ScopeLabel { get; init; }

    const string RetainedLabel = "Retained rows only";
    const string RetainedTruncatedLabel = "Retained rows only; more rows exist on the server and are not included";

    string DerivedLabel() => Truncated ? RetainedTruncatedLabel : RetainedLabel;

    /// <summary>
    /// Row indexes ordered by <paramref name="ordinal"/>. NULLs are always last (ascending and descending);
    /// ties keep their original relative order.
    /// </summary>
    public IReadOnlyList<int> SortedRowIndexes(int ordinal, bool descending)
    {
        if ((uint)ordinal >= (uint)Columns.Count)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        var keys = ColumnKeys.Build(Columns[ordinal].SqlType, Rows, ordinal);
        var order = Enumerable.Range(0, Rows.Count).ToList();
        order.Sort((a, b) =>
        {
            var aNull = keys.IsNull(a);
            var bNull = keys.IsNull(b);
            if (aNull || bNull)
                return aNull == bNull ? a.CompareTo(b) : (aNull ? 1 : -1);
            var c = keys.Compare(a, b);
            if (descending)
                c = -c;
            return c != 0 ? c : a.CompareTo(b);
        });
        return order;
    }

    /// <summary>
    /// Row indexes whose display text contains <paramref name="text"/> (case-insensitive, ordinal), in original order.
    /// Searches every column unless <paramref name="columnOrdinal"/> is given. NULL cells never match.
    /// Null/empty text matches every row.
    /// </summary>
    public IReadOnlyList<int> FilterRowIndexes(string? text, int? columnOrdinal)
    {
        if (columnOrdinal is { } col && (uint)col >= (uint)Columns.Count)
            throw new ArgumentOutOfRangeException(nameof(columnOrdinal));
        if (string.IsNullOrEmpty(text))
            return Enumerable.Range(0, Rows.Count).ToList();

        var matches = new List<int>();
        for (var r = 0; r < Rows.Count; r++)
        {
            var row = Rows[r];
            var hit = false;
            if (columnOrdinal is { } only)
                hit = only < row.Count && CellContains(row[only], text);
            else
                for (var c = 0; c < row.Count && !hit; c++)
                    hit = CellContains(row[c], text);
            if (hit)
                matches.Add(r);
        }
        return matches;
    }

    static bool CellContains(ResultCell cell, string text)
        => !cell.IsNull && cell.Display.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>A new view of the retained rows ordered by one column (see <see cref="SortedRowIndexes"/>).</summary>
    public ResultSetView SortByColumn(int ordinal, bool descending = false)
        => Select(SortedRowIndexes(ordinal, descending));

    /// <summary>A new view of the retained rows whose text contains <paramref name="text"/>.</summary>
    public ResultSetView FilterContains(string? text, int? columnOrdinal = null)
        => Select(FilterRowIndexes(text, columnOrdinal));

    ResultSetView Select(IReadOnlyList<int> indexes)
        => new()
        {
            Columns = Columns,
            Rows = indexes.Select(i => Rows[i]).ToList(),
            Truncated = Truncated,
            ScopeLabel = DerivedLabel()
        };

    /// <summary>Per-column comparison keys; numerics compare exactly (SqlDecimal, never System.Decimal/double).</summary>
    sealed class ColumnKeys
    {
        enum Mode { Text, TextIgnoreCase, Decimal, Double, Bit }

        readonly Mode _mode;
        readonly bool[] _null;
        readonly string[] _text;
        readonly SqlDecimal[] _decimal;
        readonly double[] _double;

        ColumnKeys(Mode mode, int count)
        {
            _mode = mode;
            _null = new bool[count];
            _text = new string[count];
            _decimal = new SqlDecimal[count];
            _double = new double[count];
        }

        public bool IsNull(int i) => _null[i];

        public int Compare(int a, int b) => _mode switch
        {
            Mode.Decimal => _decimal[a].CompareTo(_decimal[b]),
            Mode.Double or Mode.Bit => _double[a].CompareTo(_double[b]),
            Mode.TextIgnoreCase => string.Compare(_text[a], _text[b], StringComparison.OrdinalIgnoreCase),
            _ => string.CompareOrdinal(_text[a], _text[b])
        };

        public static ColumnKeys Build(string sqlType, IReadOnlyList<IReadOnlyList<ResultCell>> rows, int ordinal)
        {
            var keys = Fill(ModeFor(sqlType), rows, ordinal);
            // A value that does not parse as the declared type falls back to text for the whole column.
            return keys ?? Fill(IsTextual(sqlType) ? Mode.TextIgnoreCase : Mode.Text, rows, ordinal)!;
        }

        static Mode ModeFor(string sqlType) => sqlType.ToLowerInvariant() switch
        {
            "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney" => Mode.Decimal,
            "float" or "real" => Mode.Double,
            "bit" => Mode.Bit,
            _ when IsTextual(sqlType) => Mode.TextIgnoreCase,
            _ => Mode.Text
        };

        static bool IsTextual(string sqlType)
            => sqlType.Contains("char", StringComparison.OrdinalIgnoreCase) ||
               sqlType.Contains("text", StringComparison.OrdinalIgnoreCase) ||
               sqlType.Contains("xml", StringComparison.OrdinalIgnoreCase);

        static ColumnKeys? Fill(Mode mode, IReadOnlyList<IReadOnlyList<ResultCell>> rows, int ordinal)
        {
            var keys = new ColumnKeys(mode, rows.Count);
            for (var r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                if (ordinal >= row.Count || row[ordinal].IsNull)
                {
                    keys._null[r] = true;
                    continue;
                }
                var display = row[ordinal].Display;
                switch (mode)
                {
                    case Mode.Decimal:
                        try { keys._decimal[r] = SqlDecimal.Parse(display); }
                        catch (Exception ex) when (ex is FormatException or OverflowException or SqlTypeException) { return null; }
                        break;
                    case Mode.Double:
                        if (!double.TryParse(display, NumberStyles.Float, CultureInfo.InvariantCulture, out keys._double[r]))
                            return null;
                        break;
                    case Mode.Bit:
                        if (display.Equals("true", StringComparison.OrdinalIgnoreCase) || display == "1")
                            keys._double[r] = 1;
                        else if (display.Equals("false", StringComparison.OrdinalIgnoreCase) || display == "0")
                            keys._double[r] = 0;
                        else
                            return null;
                        break;
                    default:
                        keys._text[r] = display;
                        break;
                }
            }
            return keys;
        }
    }
}
