using System.Data.Common;
using System.Data.SqlTypes;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Sqloryn.Core;

/// <summary>Bounds cell and aggregate retention while preserving completed rows.</summary>
public sealed class ResultBudget
{
    public int RetainedChars { get; private set; }
    public bool Exceeded { get; private set; }

    public bool TryAdd(int chars)
    {
        if (Exceeded)
            return false;
        if (RetainedChars + chars > QueryLimits.MaxRetainedOutputChars)
        {
            Exceeded = true;
            return false;
        }
        RetainedChars += chars;
        return true;
    }
}

public static class ResultMaterializer
{
    public static async Task<ResultSet> ReadResultSetAsync(
        DbDataReader reader,
        ResultBudget budget,
        CancellationToken cancellationToken)
    {
        var columns = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            columns[i] = string.IsNullOrEmpty(reader.GetName(i)) ? $"Column{i + 1}" : reader.GetName(i);

        var rows = new List<IReadOnlyList<CellValue>>();
        var truncated = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (budget.Exceeded || rows.Count >= QueryLimits.MaxRowsPerSet)
            {
                truncated = true;
                break;
            }

            var cells = new CellValue[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                cells[i] = FormatCell(reader, i);
                if (!budget.TryAdd(cells[i].Display.Length))
                {
                    cells[i] = CellValue.Text(cells[i].Display, cells[i].Kind, truncated: true);
                    truncated = true;
                    // Keep this cell (already read) but stop further rows/columns budget growth.
                    break;
                }
            }
            rows.Add(cells);
            if (budget.Exceeded)
            {
                truncated = true;
                break;
            }
        }

        return new ResultSet
        {
            Columns = columns,
            Rows = rows,
            Truncated = truncated,
            BudgetExceeded = budget.Exceeded
        };
    }

    internal static CellValue FormatCell(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return CellValue.Null;

        if (reader is SqlDataReader sqlReader)
        {
            var typeName = sqlReader.GetDataTypeName(ordinal);
            if (typeName.Contains("decimal", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("numeric", StringComparison.OrdinalIgnoreCase))
            {
                var sqlDecimal = sqlReader.GetSqlDecimal(ordinal);
                if (sqlDecimal.IsNull)
                    return CellValue.Null;
                return FormatSqlDecimal(sqlDecimal.Value);
            }

            if (IsCharacterType(typeName))
                return ReadBoundedChars(sqlReader, ordinal);
            if (IsBinaryType(typeName))
                return ReadBoundedBinary(sqlReader, ordinal);
            if (typeName.Contains("datetimeoffset", StringComparison.OrdinalIgnoreCase))
                return CellValue.Text(sqlReader.GetDateTimeOffset(ordinal).ToString("o", CultureInfo.InvariantCulture), "datetimeoffset");
        }

        // Fallback path (tests / non-SqlClient readers). Prefer GetChars/GetBytes when available.
        var fieldType = reader.GetFieldType(ordinal);
        if (fieldType == typeof(string) || fieldType == typeof(char))
            return ReadBoundedChars(reader, ordinal);
        if (fieldType == typeof(byte[]) || fieldType == typeof(SqlBinary))
            return ReadBoundedBinary(reader, ordinal);
        if (fieldType == typeof(decimal))
        {
            // System.Decimal cannot hold 38 digits; still format what we have and mark kind.
            var d = reader.GetDecimal(ordinal);
            return CellValue.Text(d.ToString(CultureInfo.InvariantCulture), "decimal");
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            string s => BoundText(s, s.Length == 0 ? "empty-string" : "string"),
            char c => CellValue.Text(c.ToString(), "string"),
            byte[] bytes => BoundBinary(bytes),
            DateTimeOffset dto => CellValue.Text(dto.ToString("o", CultureInfo.InvariantCulture), "datetimeoffset"),
            DateTime dt => CellValue.Text(dt.ToString("o", CultureInfo.InvariantCulture), "datetime"),
            bool b => CellValue.Text(b ? "true" : "false", "bool"),
            SqlDecimal sd => FormatSqlDecimal(sd),
            IFormattable formattable => BoundText(formattable.ToString(null, CultureInfo.InvariantCulture) ?? "", "number"),
            _ => BoundText(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", "other")
        };
    }

    internal static CellValue FormatSqlDecimal(SqlDecimal value)
        => CellValue.Text(value.ToString(), "decimal");

    static bool IsCharacterType(string typeName)
        => typeName.Contains("char", StringComparison.OrdinalIgnoreCase) ||
           typeName.Contains("text", StringComparison.OrdinalIgnoreCase) ||
           typeName.Contains("xml", StringComparison.OrdinalIgnoreCase);

    static bool IsBinaryType(string typeName)
        => typeName.Contains("binary", StringComparison.OrdinalIgnoreCase) ||
           typeName.Contains("image", StringComparison.OrdinalIgnoreCase);

    static CellValue ReadBoundedChars(DbDataReader reader, int ordinal)
    {
        try
        {
            var total = reader.GetChars(ordinal, 0, null, 0, 0);
            if (total == 0)
                return CellValue.Text("", "empty-string");
            var take = (int)Math.Min(total, QueryLimits.MaxCellChars);
            var buffer = new char[take];
            reader.GetChars(ordinal, 0, buffer, 0, take);
            var truncated = total > QueryLimits.MaxCellChars;
            var text = new string(buffer);
            return CellValue.Text(truncated ? text + "…" : text, text.Length == 0 ? "empty-string" : "string", truncated);
        }
        catch (InvalidCastException)
        {
            var s = reader.GetString(ordinal);
            return BoundText(s, s.Length == 0 ? "empty-string" : "string");
        }
    }

    static CellValue ReadBoundedBinary(DbDataReader reader, int ordinal)
    {
        try
        {
            var total = reader.GetBytes(ordinal, 0, null, 0, 0);
            var maxBytes = QueryLimits.MaxCellChars / 2;
            var take = (int)Math.Min(total, maxBytes);
            var buffer = new byte[take];
            if (take > 0)
                reader.GetBytes(ordinal, 0, buffer, 0, take);
            var truncated = total > maxBytes;
            return CellValue.Text(
                "0x" + Convert.ToHexString(buffer) + (truncated ? "…" : ""),
                "binary",
                truncated);
        }
        catch (InvalidCastException)
        {
            return BoundBinary(reader.GetFieldValue<byte[]>(ordinal));
        }
    }

    static CellValue BoundText(string value, string kind)
    {
        if (value.Length <= QueryLimits.MaxCellChars)
            return CellValue.Text(value, kind);
        return CellValue.Text(value[..QueryLimits.MaxCellChars] + "…", kind, truncated: true);
    }

    static CellValue BoundBinary(byte[] bytes)
    {
        var maxBytes = QueryLimits.MaxCellChars / 2;
        var truncated = bytes.Length > maxBytes;
        var take = Math.Min(bytes.Length, maxBytes);
        return CellValue.Text(
            "0x" + Convert.ToHexString(bytes.AsSpan(0, take)) + (truncated ? "…" : ""),
            "binary",
            truncated);
    }

    public static string RenderCell(CellValue cell)
        => cell.IsNull ? "NULL" : (cell.Truncated ? cell.Display : cell.Display);
}
