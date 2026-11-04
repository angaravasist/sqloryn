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
        var types = sqlTypes ?? set.SqlTypes;
        var columns = set.Columns
            .Select((name, i) =>
            {
                var sqlType = types is not null && i < types.Count ? types[i] : "";
                if (string.IsNullOrEmpty(sqlType))
                    sqlType = InferSqlTypeFromKind(set, i);
                return new ResultColumn(name, sqlType);
            })
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

    /// <summary>Maps retained cell kinds to export SQL type hints when the provider type list is empty.</summary>
    static string InferSqlTypeFromKind(ResultSet set, int ordinal)
    {
        foreach (var row in set.Rows)
        {
            if (ordinal >= row.Count || row[ordinal].IsNull)
                continue;
            return row[ordinal].Kind switch
            {
                "number" => "int",
                "bool" => "bit",
                "datetime" or "datetimeoffset" or "date" or "money" or "smallmoney" => row[ordinal].Kind,
                "empty-string" or "text" => "nvarchar",
                "binary" => "varbinary",
                "null" => "",
                _ => ""
            };
        }
        return "";
    }
}

public sealed record CsvOptions
{
    /// <summary>
    /// Prefix text cells that a spreadsheet would treat as formulas (leading = + - @ tab or CR) with an apostrophe.
    /// Numeric-typed cells that parse as numbers (e.g. -5) are left alone. Changes cell text, so it is opt-in.
    /// </summary>
    public bool SpreadsheetSafe { get; init; }
}

/// <summary>
/// RFC 4180 CSV with CRLF line endings. NULL is an unquoted empty field; an empty string is written as "".
/// </summary>
public static class CsvExporter
{
    static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "float", "real", "money", "smallmoney"
    };

    public static void Write(TextWriter writer, ResultSetView view, CsvOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(view);
        var safe = options?.SpreadsheetSafe == true;

        for (var i = 0; i < view.Columns.Count; i++)
        {
            if (i > 0)
                writer.Write(',');
            var name = view.Columns[i].Name;
            writer.Write(Quote(safe ? Neutralise(name, "") : name, forceQuoteEmpty: true));
        }
        writer.Write("\r\n");

        foreach (var row in view.Rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                    writer.Write(',');
                if (row[i].IsNull)
                    continue;
                var value = row[i].Display;
                if (safe)
                    value = Neutralise(value, i < view.Columns.Count ? view.Columns[i].SqlType : "");
                writer.Write(Quote(value, forceQuoteEmpty: true));
            }
            writer.Write("\r\n");
        }
    }

    public static string ToCsv(ResultSetView view, CsvOptions? options = null)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(writer, view, options);
        return writer.ToString();
    }

    static string Neutralise(string value, string sqlType)
    {
        if (value.Length == 0 || value[0] is not ('=' or '+' or '-' or '@' or '\t' or '\r'))
            return value;
        // Prefer typed columns; also leave alone values that are plain numbers when type is missing
        // so UI paths that omit SqlTypes do not corrupt negatives like -5.
        var typedNumeric = NumericTypes.Contains(sqlType);
        var looksNumeric = value[0] is '+' or '-' &&
                           decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        if ((typedNumeric || string.IsNullOrEmpty(sqlType)) && looksNumeric)
            return value;
        if (typedNumeric &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return value;
        return "'" + value;
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
/// Loss-aware JSON: <c>{ "format", "resultSets": [ { "ordinal", "columns": [ { "ordinal", "name", "sqlType" } ],
/// "rows": [ [cell, ...] ], "truncated" } ] }</c>. Columns are addressed by ordinal, so duplicate or blank
/// labels are preserved (an object keyed by label would drop them). NULL is JSON null. Only
/// tinyint/smallint/int and bit map to JSON number/boolean; bigint, decimal, numeric, money and floating point
/// are emitted as strings to preserve precision.
/// </summary>
public static class JsonExporter
{
    public const string FormatId = "sqloryn.result-sets.v1";

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
        return ToJson([view]);
    }

    public static string ToJson(IReadOnlyList<ResultSetView> views)
    {
        ArgumentNullException.ThrowIfNull(views);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("format", FormatId);
            json.WritePropertyName("resultSets");
            json.WriteStartArray();
            for (var s = 0; s < views.Count; s++)
            {
                var view = views[s];
                json.WriteStartObject();
                json.WriteNumber("ordinal", s);
                json.WritePropertyName("columns");
                json.WriteStartArray();
                for (var i = 0; i < view.Columns.Count; i++)
                {
                    json.WriteStartObject();
                    json.WriteNumber("ordinal", i);
                    json.WriteString("name", view.Columns[i].Name);
                    json.WriteString("sqlType", view.Columns[i].SqlType);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WritePropertyName("rows");
                json.WriteStartArray();
                foreach (var row in view.Rows)
                {
                    json.WriteStartArray();
                    for (var i = 0; i < view.Columns.Count; i++)
                    {
                        if (i < row.Count)
                            WriteCell(json, row[i], view.Columns[i].SqlType);
                        else
                            json.WriteNullValue();
                    }
                    json.WriteEndArray();
                }
                json.WriteEndArray();
                json.WriteBoolean("truncated", view.Truncated);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
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

public enum ResultExportFormat
{
    Csv,
    Json
}

public sealed record ResultExportOptions
{
    /// <summary>CSV only: neutralise formula-like text cells (see <see cref="CsvOptions.SpreadsheetSafe"/>).</summary>
    public bool SpreadsheetSafeCsv { get; init; }
}

public sealed record ResultExportOutcome(string Path, int ResultSetCount, bool Incomplete);

/// <summary>
/// Exports to exactly ONE caller-chosen destination. Never derives sibling file names, so no other file is
/// touched. CSV carries one result set; JSON carries any number in a single document. Writes are staged and
/// replaced via <see cref="SafeFileWriter"/>.
/// </summary>
public static class ResultExportWriter
{
    public static string Render(ResultExportFormat format, IReadOnlyList<ResultSetView> views, ResultExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(views);
        if (views.Count == 0)
            throw new ArgumentException("Nothing to export.", nameof(views));
        switch (format)
        {
            case ResultExportFormat.Csv:
                if (views.Count != 1)
                    throw new ArgumentException("CSV holds one result set per file; choose a single result set.", nameof(views));
                return CsvExporter.ToCsv(views[0], new CsvOptions { SpreadsheetSafe = options?.SpreadsheetSafeCsv == true });
            case ResultExportFormat.Json:
                return JsonExporter.ToJson(views);
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public static async Task<ResultExportOutcome> ExportAsync(
        string path,
        ResultExportFormat format,
        IReadOnlyList<ResultSetView> views,
        ResultExportOptions? options = null,
        CancellationToken cancellationToken = default,
        IStagedFileCommitter? committer = null,
        Func<CancellationToken, Task>? beforeCommit = null)
    {
        var text = Render(format, views, options);
        await SafeFileWriter.WriteUtf8Async(path, text, cancellationToken, committer, beforeCommit).ConfigureAwait(false);
        return new ResultExportOutcome(path, views.Count, views.Any(v => v.Truncated));
    }
}
