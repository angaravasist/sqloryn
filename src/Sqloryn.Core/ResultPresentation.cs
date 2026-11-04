using System.Globalization;
using System.Text;

namespace Sqloryn.Core;

/// <summary>
/// Monospace text for rows already retained in memory. It does not run SQL.
/// SQL NULL is <see cref="NullMarker"/>, which is distinct from a blank empty string and from the word NULL.
/// </summary>
public static class ResultTextFormatter
{
    public const string NullMarker = "(null)";

    public static string CellToken(CellValue cell)
    {
        if (cell.IsNull)
            return NullMarker;
        return cell.Display
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Replace('\t', ' ');
    }

    /// <summary>Aligned text of at most <paramref name="maxRows"/> retained rows, in retained order.</summary>
    public static string Format(ResultSet set, int maxRows)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (maxRows < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRows));

        var shown = Math.Min(set.Rows.Count, maxRows);
        var columns = set.Columns.Count;
        var headers = new string[columns];
        var widths = new int[columns];
        for (var c = 0; c < columns; c++)
        {
            headers[c] = string.IsNullOrEmpty(set.Columns[c]) ? "(no column name)" : set.Columns[c];
            widths[c] = headers[c].Length;
        }

        var tokens = new string[shown][];
        for (var r = 0; r < shown; r++)
        {
            tokens[r] = new string[columns];
            var row = set.Rows[r];
            for (var c = 0; c < columns; c++)
            {
                var cell = c < row.Count ? row[c] : CellValue.Null;
                var token = CellToken(cell);
                tokens[r][c] = token;
                if (token.Length > widths[c])
                    widths[c] = token.Length;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("Retained rows as text. Does not re-query the server.");
        if (columns == 0)
        {
            sb.AppendLine("(no columns)");
            return sb.ToString();
        }

        AppendAligned(sb, headers, widths);
        AppendRule(sb, widths);
        for (var r = 0; r < shown; r++)
            AppendAligned(sb, tokens[r], widths);
        if (shown < set.Rows.Count)
        {
            sb.Append("… displaying first ")
                .Append(shown.ToString(CultureInfo.InvariantCulture))
                .Append(" of ")
                .Append(set.Rows.Count.ToString(CultureInfo.InvariantCulture))
                .AppendLine(" retained rows.");
        }
        return sb.ToString();
    }

    static void AppendAligned(StringBuilder sb, string[] cells, int[] widths)
    {
        for (var c = 0; c < cells.Length; c++)
        {
            if (c > 0)
                sb.Append("  ");
            sb.Append(cells[c].PadRight(widths[c]));
        }
        sb.AppendLine();
    }

    static void AppendRule(StringBuilder sb, int[] widths)
    {
        for (var c = 0; c < widths.Length; c++)
        {
            if (c > 0)
                sb.Append("  ");
            sb.Append(new string('-', Math.Max(widths[c], 1)));
        }
        sb.AppendLine();
    }
}

/// <summary>
/// Clipboard text for retained cells. Matches CSV export: SQL NULL is an empty field (not the display word NULL)
/// and an empty string is written as <c>""</c>.
/// </summary>
public static class ResultCopy
{
    public static string FormatCell(CellValue cell)
        => cell.IsNull ? "" : cell.Display.Length == 0 ? "\"\"" : cell.Display;

    public static string FormatGrid(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<CellValue>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join('\t', headers));
        foreach (var row in rows)
            sb.AppendLine(string.Join('\t', row.Select(FormatCell)));
        return sb.ToString();
    }
}

/// <summary>
/// Provider <c>RecordsAffected</c>. Negative values, including SqlClient's -1 for statements that do not report a count,
/// are omitted. Zero is kept. Counts are never guessed from retained grid rows.
/// </summary>
public static class ProviderRowsAffected
{
    public static int? FromRecordsAffected(int recordsAffected)
        => recordsAffected >= 0 ? recordsAffected : null;

    public static int? SumReported(IEnumerable<int?> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        int? sum = null;
        foreach (var count in counts)
        {
            if (count is not int n || n < 0)
                continue;
            sum = (sum ?? 0) + n;
        }
        return sum;
    }
}
