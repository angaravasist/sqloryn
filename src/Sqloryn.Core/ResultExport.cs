using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sqloryn.Core;

public sealed record ResultColumn(string Name, string SqlType);

public readonly record struct ResultCell(string Display, bool IsNull)
{
    public static ResultCell Null => new("", true);
    public static ResultCell Of(string display) => new(display ?? "", false);
}

public sealed class ResultSetView
{
    public required IReadOnlyList<ResultColumn> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<ResultCell>> Rows { get; init; }
    /// <summary>True when retained rows or cells were truncated; an export of this view is incomplete.</summary>
    public bool Truncated { get; init; }

    public static ResultSetView FromResultSet(ResultSet set, IReadOnlyList<string>? sqlTypes = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        var columns = set.Columns
            .Select((name, i) => new ResultColumn(name, sqlTypes is not null && i < sqlTypes.Count ? sqlTypes[i] : ""))
            .ToList();
        var anyCellTruncated = false;
        var rows = new List<IReadOnlyList<ResultCell>>(set.Rows.Count);
        foreach (var row in set.Rows)
        {
            var cells = new List<ResultCell>(row.Count);
            foreach (var cell in row)
            {
                anyCellTruncated |= cell.Truncated;
                cells.Add(cell.IsNull ? ResultCell.Null : ResultCell.Of(cell.Display));
            }
            rows.Add(cells);
        }
        return new ResultSetView { Columns = columns, Rows = rows, Truncated = set.Truncated || set.BudgetExceeded || anyCellTruncated };
    }
}

/// <summary>
/// RFC 4180 CSV with CRLF line endings. NULL is an unquoted empty field; an empty string is written as "".
/// </summary>
public static class CsvExporter
{
    public static void Write(TextWriter writer, ResultSetView view)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(view);

        for (var i = 0; i < view.Columns.Count; i++)
        {
            if (i > 0)
                writer.Write(',');
            writer.Write(Quote(view.Columns[i].Name, forceQuoteEmpty: true));
        }
        writer.Write("\r\n");

        foreach (var row in view.Rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                    writer.Write(',');
                if (!row[i].IsNull)
                    writer.Write(Quote(row[i].Display, forceQuoteEmpty: true));
            }
            writer.Write("\r\n");
        }
    }

    public static string ToCsv(ResultSetView view)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(writer, view);
        return writer.ToString();
    }

    static string Quote(string value, bool forceQuoteEmpty)
    {
        if (value.Length == 0)
            return forceQuoteEmpty ? "\"\"" : "";
        var needsQuote = value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ||
                         char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]);
        return needsQuote ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}

/// <summary>
/// JSON array of objects. NULL is JSON null. Only tinyint/smallint/int and bit map to JSON number/boolean;
/// bigint, decimal, numeric, money and floating point are emitted as strings to preserve precision.
/// </summary>
public static class JsonExporter
{
    static readonly HashSet<string> SafeIntegerTypes = new(StringComparer.OrdinalIgnoreCase) { "tinyint", "smallint", "int" };

    public static void Write(TextWriter writer, ResultSetView view)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(view);
        writer.Write(ToJson(view));
    }

    public static string ToJson(ResultSetView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartArray();
            foreach (var row in view.Rows)
            {
                json.WriteStartObject();
                for (var i = 0; i < view.Columns.Count && i < row.Count; i++)
                {
                    json.WritePropertyName(view.Columns[i].Name);
                    WriteCell(json, row[i], view.Columns[i].SqlType);
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        return new UTF8Encoding(false).GetString(stream.ToArray());
    }

    static void WriteCell(Utf8JsonWriter json, ResultCell cell, string sqlType)
    {
        if (cell.IsNull)
        {
            json.WriteNullValue();
            return;
        }

        if (SafeIntegerTypes.Contains(sqlType) &&
            long.TryParse(cell.Display, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            json.WriteNumberValue(number);
            return;
        }

        if (sqlType.Equals("bit", StringComparison.OrdinalIgnoreCase))
        {
            if (cell.Display.Equals("true", StringComparison.OrdinalIgnoreCase) || cell.Display == "1")
            {
                json.WriteBooleanValue(true);
                return;
            }
            if (cell.Display.Equals("false", StringComparison.OrdinalIgnoreCase) || cell.Display == "0")
            {
                json.WriteBooleanValue(false);
                return;
            }
        }

        json.WriteStringValue(cell.Display);
    }
}
