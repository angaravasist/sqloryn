using System.Data.Common;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Sqloryn.Core;

/// <summary>Bounds cell and aggregate retention while preserving completed rows only.</summary>
public sealed class ResultBudget
{
    public int RetainedChars { get; private set; }
    public bool Exceeded { get; private set; }

    public int ChargeFor(CellValue cell)
    {
        var display = cell.IsNull ? 4 : Math.Max(cell.Display.Length, 0);
        return Math.Max(QueryLimits.MinCellBudgetChars, display);
    }

    public bool TryReserve(int chars)
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

    /// <summary>True when <paramref name="chars"/> could still be reserved without reading cell values.</summary>
    public bool HasCapacity(int chars)
        => !Exceeded && RetainedChars + chars <= QueryLimits.MaxRetainedOutputChars;

    public void MarkExceeded() => Exceeded = true;
}

public sealed class ResultSetReadOutcome
{
    public required ResultSet Set { get; init; }
    public Exception? RowFailure { get; init; }
}

/// <summary>
/// Production SqlDataReader cell source. Never converts decimal/numeric through System.Decimal.
/// </summary>
public interface ISqlTypedCellSource
{
    int FieldCount { get; }
    string GetName(int ordinal);
    bool IsDBNull(int ordinal);
    string GetDataTypeName(int ordinal);
    SqlDecimal GetSqlDecimal(int ordinal);
    DateTimeOffset GetDateTimeOffset(int ordinal);
    /// <summary>money/smallmoney via SqlMoney (never System.Decimal / double).</summary>
    SqlMoney GetSqlMoney(int ordinal);
    /// <summary>SQL <c>date</c> column value (time-of-day component is meaningless and ignored).</summary>
    DateTime GetDate(int ordinal);
    long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length);
    long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length);
    bool TryGetBoolean(int ordinal, out bool value);
    bool TryGetDateTime(int ordinal, out DateTime value);
    bool TryGetFormattableNumber(int ordinal, out string text);
}

public sealed class SqlDataReaderCellSource(SqlDataReader reader) : ISqlTypedCellSource
{
    public int FieldCount => reader.FieldCount;
    public string GetName(int ordinal) => reader.GetName(ordinal);
    public bool IsDBNull(int ordinal) => reader.IsDBNull(ordinal);
    public string GetDataTypeName(int ordinal) => reader.GetDataTypeName(ordinal);
    public SqlDecimal GetSqlDecimal(int ordinal) => reader.GetSqlDecimal(ordinal);
    public DateTimeOffset GetDateTimeOffset(int ordinal) => reader.GetDateTimeOffset(ordinal);
    public SqlMoney GetSqlMoney(int ordinal) => reader.GetSqlMoney(ordinal);
    public DateTime GetDate(int ordinal) => reader.GetDateTime(ordinal);
    public long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => reader.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    public long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => reader.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

    public bool TryGetBoolean(int ordinal, out bool value)
    {
        if (reader.GetFieldType(ordinal) == typeof(bool))
        {
            value = reader.GetBoolean(ordinal);
            return true;
        }
        value = default;
        return false;
    }

    public bool TryGetDateTime(int ordinal, out DateTime value)
    {
        var typeName = reader.GetDataTypeName(ordinal);
        if (typeName.Contains("datetime", StringComparison.OrdinalIgnoreCase) &&
            !typeName.Contains("datetimeoffset", StringComparison.OrdinalIgnoreCase))
        {
            value = reader.GetDateTime(ordinal);
            return true;
        }
        value = default;
        return false;
    }

    public bool TryGetFormattableNumber(int ordinal, out string text)
    {
        var type = reader.GetFieldType(ordinal);
        if (type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long) ||
            type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            // Integers/floats only — decimal/numeric already handled via GetSqlDecimal.
            if (type == typeof(decimal))
            {
                text = "";
                return false;
            }
            var value = reader.GetValue(ordinal);
            text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            return true;
        }
        text = "";
        return false;
    }
}

public static class ResultMaterializer
{
    public static async Task<ResultSetReadOutcome> ReadResultSetAsync(
        DbDataReader reader,
        ResultBudget budget,
        CancellationToken cancellationToken)
    {
        var columns = new string[reader.FieldCount];
        var sqlTypes = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns[i] = string.IsNullOrEmpty(reader.GetName(i)) ? $"Column{i + 1}" : reader.GetName(i);
            try { sqlTypes[i] = reader.GetDataTypeName(i) ?? ""; }
            catch (Exception) { sqlTypes[i] = ""; }
        }

        var rows = new List<IReadOnlyList<CellValue>>();
        var truncated = false;
        Exception? rowFailure = null;
        ISqlTypedCellSource? sqlSource = reader is SqlDataReader sqlReader
            ? new SqlDataReaderCellSource(sqlReader)
            : null;

        while (true)
        {
            bool hasRow;
            try
            {
                hasRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Preserve fully read rows even when cancellation/timeout stops further reading.
                rowFailure = ex;
                truncated = true;
                break;
            }

            if (!hasRow)
                break;

            if (budget.Exceeded || rows.Count >= QueryLimits.MaxRowsPerSet)
            {
                truncated = true;
                budget.MarkExceeded();
                // Do not materialise discarded cells — BatchObserver drains by Read only.
                break;
            }

            // Stop before touching cell values when the next row cannot fit.
            if (!budget.HasCapacity(QueryLimits.MinCellBudgetChars * Math.Max(1, reader.FieldCount)))
            {
                truncated = true;
                budget.MarkExceeded();
                break;
            }

            try
            {
                var cells = new CellValue[reader.FieldCount];
                var rowOk = true;
                var ordinal = 0;
                for (; ordinal < reader.FieldCount; ordinal++)
                {
                    var cell = sqlSource is not null
                        ? FormatSqlTypedCell(sqlSource, ordinal)
                        : FormatFallbackCell(reader, ordinal);
                    var charge = budget.ChargeFor(cell);
                    if (!budget.TryReserve(charge))
                    {
                        rowOk = false;
                        truncated = true;
                        break;
                    }
                    cells[ordinal] = cell;
                }

                if (!rowOk)
                {
                    // Incomplete row omitted without reading remaining discarded cells.
                    // Prior complete rows kept; BatchObserver drains by advancing Read only.
                    break;
                }

                rows.Add(cells);
            }
            catch (Exception ex)
            {
                rowFailure = ex;
                truncated = true;
                break;
            }
        }

        return new ResultSetReadOutcome
        {
            Set = new ResultSet
            {
                Columns = columns,
                SqlTypes = sqlTypes,
                Rows = rows,
                Truncated = truncated || budget.Exceeded || rowFailure is not null,
                BudgetExceeded = budget.Exceeded
            },
            RowFailure = rowFailure
        };
    }

    /// <summary>Production path for SqlDataReader — SqlDecimal stays SqlDecimal (no System.Decimal).</summary>
    public static CellValue FormatSqlTypedCell(ISqlTypedCellSource source, int ordinal)
    {
        if (source.IsDBNull(ordinal))
            return CellValue.Null;

        var typeName = source.GetDataTypeName(ordinal);
        if (typeName.Contains("decimal", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("numeric", StringComparison.OrdinalIgnoreCase))
        {
            var sqlDecimal = source.GetSqlDecimal(ordinal);
            if (sqlDecimal.IsNull)
                return CellValue.Null;
            return FormatSqlDecimal(sqlDecimal);
        }

        if (IsMoneyType(typeName, out var moneyKind))
        {
            var money = source.GetSqlMoney(ordinal);
            return money.IsNull ? CellValue.Null : FormatSqlMoney(money, moneyKind);
        }

        if (typeName.Equals("date", StringComparison.OrdinalIgnoreCase))
            return FormatDate(source.GetDate(ordinal));

        if (IsCharacterType(typeName))
            return ReadBoundedChars(source, ordinal);
        if (IsBinaryType(typeName))
            return ReadBoundedBinary(source, ordinal);
        if (typeName.Contains("datetimeoffset", StringComparison.OrdinalIgnoreCase))
            return CellValue.Text(source.GetDateTimeOffset(ordinal).ToString("o", CultureInfo.InvariantCulture), "datetimeoffset");
        if (source.TryGetDateTime(ordinal, out var dt))
            return CellValue.Text(dt.ToString("o", CultureInfo.InvariantCulture), "datetime");
        if (source.TryGetBoolean(ordinal, out var b))
            return CellValue.Text(b ? "true" : "false", "bool");
        if (source.TryGetFormattableNumber(ordinal, out var numberText))
            return BoundText(numberText, "number");

        // Unsupported large/LOB types without a bounded API: refuse full-value materialisation.
        throw new NotSupportedException(
            $"Cannot materialise column type '{typeName}' without a bounded sequential reader.");
    }

    /// <summary>Formats SqlDecimal without converting through System.Decimal.</summary>
    public static CellValue FormatSqlDecimal(SqlDecimal value)
        => CellValue.Text(value.ToString(), "decimal");

    /// <summary>money/smallmoney with exactly four fractional digits, invariant formatting, sign preserved.</summary>
    public static CellValue FormatSqlMoney(SqlMoney value, string kind = "money")
        => CellValue.Text(value.ToSqlDecimal().ToString(), kind);

    /// <summary>SQL <c>date</c> as ISO yyyy-MM-dd (no time component).</summary>
    public static CellValue FormatDate(DateTime value)
        => CellValue.Text(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "date");

    static bool IsMoneyType(string typeName, out string kind)
    {
        if (typeName.Equals("smallmoney", StringComparison.OrdinalIgnoreCase))
        {
            kind = "smallmoney";
            return true;
        }
        if (typeName.Equals("money", StringComparison.OrdinalIgnoreCase))
        {
            kind = "money";
            return true;
        }
        kind = "";
        return false;
    }

    /// <summary>Non-SqlClient readers (unit fixtures only). Not used for live SqlDataReader decimal(38).</summary>
    internal static CellValue FormatCell(DbDataReader reader, int ordinal)
        => reader is SqlDataReader sql
            ? FormatSqlTypedCell(new SqlDataReaderCellSource(sql), ordinal)
            : FormatFallbackCell(reader, ordinal);

    static CellValue FormatFallbackCell(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return CellValue.Null;

        var fieldType = reader.GetFieldType(ordinal);
        if (fieldType == typeof(string) || fieldType == typeof(char))
            return ReadBoundedCharsDb(reader, ordinal);
        if (fieldType == typeof(byte[]))
            return ReadBoundedBinaryDb(reader, ordinal);
        if (fieldType == typeof(SqlDecimal))
            return FormatSqlDecimal(reader.GetFieldValue<SqlDecimal>(ordinal));
        if (fieldType == typeof(SqlMoney))
        {
            var typeName = SafeTypeName(reader, ordinal);
            IsMoneyType(typeName, out var kind);
            return FormatSqlMoney(reader.GetFieldValue<SqlMoney>(ordinal), kind.Length == 0 ? "money" : kind);
        }
        if (fieldType == typeof(bool))
            return CellValue.Text(reader.GetBoolean(ordinal) ? "true" : "false", "bool");
        if (fieldType == typeof(DateTime) && SafeTypeName(reader, ordinal).Equals("date", StringComparison.OrdinalIgnoreCase))
            return FormatDate(reader.GetDateTime(ordinal));
        if (fieldType == typeof(DateTimeOffset))
            return CellValue.Text(reader.GetFieldValue<DateTimeOffset>(ordinal).ToString("o", CultureInfo.InvariantCulture), "datetimeoffset");
        if (fieldType == typeof(DateTime))
            return CellValue.Text(reader.GetDateTime(ordinal).ToString("o", CultureInfo.InvariantCulture), "datetime");
        if (fieldType == typeof(byte) || fieldType == typeof(short) || fieldType == typeof(int) || fieldType == typeof(long) ||
            fieldType == typeof(float) || fieldType == typeof(double))
        {
            var value = reader.GetValue(ordinal);
            return BoundText(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", "number");
        }

        // No System.Decimal path for 38-digit fidelity — fixtures must supply SqlDecimal or use ISqlTypedCellSource.
        throw new NotSupportedException(
            $"Fallback reader cannot format '{fieldType.Name}' without a bounded typed accessor.");
    }

    static string SafeTypeName(DbDataReader reader, int ordinal)
    {
        try { return reader.GetDataTypeName(ordinal) ?? ""; }
        catch (Exception) { return ""; }
    }

    static bool IsCharacterType(string typeName)
        => typeName.Contains("char", StringComparison.OrdinalIgnoreCase) ||
           typeName.Contains("text", StringComparison.OrdinalIgnoreCase) ||
           typeName.Contains("xml", StringComparison.OrdinalIgnoreCase);

    static bool IsBinaryType(string typeName)
        => typeName.Contains("binary", StringComparison.OrdinalIgnoreCase) ||
           typeName.Contains("image", StringComparison.OrdinalIgnoreCase);

    static CellValue ReadBoundedChars(ISqlTypedCellSource source, int ordinal)
    {
        var total = source.GetChars(ordinal, 0, null, 0, 0);
        if (total == 0)
            return CellValue.Text("", "empty-string");
        var take = (int)Math.Min(total, QueryLimits.MaxCellChars);
        var buffer = new char[take];
        source.GetChars(ordinal, 0, buffer, 0, take);
        var truncated = total > QueryLimits.MaxCellChars;
        var text = new string(buffer);
        return CellValue.Text(truncated ? text + "…" : text, text.Length == 0 ? "empty-string" : "string", truncated);
    }

    static CellValue ReadBoundedBinary(ISqlTypedCellSource source, int ordinal)
    {
        var total = source.GetBytes(ordinal, 0, null, 0, 0);
        var maxBytes = QueryLimits.MaxCellChars / 2;
        var take = (int)Math.Min(total, maxBytes);
        var buffer = new byte[take];
        if (take > 0)
            source.GetBytes(ordinal, 0, buffer, 0, take);
        var truncated = total > maxBytes;
        return CellValue.Text(
            "0x" + Convert.ToHexString(buffer) + (truncated ? "…" : ""),
            "binary",
            truncated);
    }

    static CellValue ReadBoundedCharsDb(DbDataReader reader, int ordinal)
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
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException)
        {
            // Fixture readers (e.g. DataTableReader) may lack GetChars; production uses ISqlTypedCellSource.
            var s = reader.GetString(ordinal);
            return BoundText(s, s.Length == 0 ? "empty-string" : "string");
        }
    }

    static CellValue ReadBoundedBinaryDb(DbDataReader reader, int ordinal)
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
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException)
        {
            var bytes = reader.GetFieldValue<byte[]>(ordinal);
            var maxBytes = QueryLimits.MaxCellChars / 2;
            var truncated = bytes.Length > maxBytes;
            var take = Math.Min(bytes.Length, maxBytes);
            return CellValue.Text(
                "0x" + Convert.ToHexString(bytes.AsSpan(0, take)) + (truncated ? "…" : ""),
                "binary",
                truncated);
        }
    }

    static CellValue BoundText(string value, string kind)
    {
        if (value.Length <= QueryLimits.MaxCellChars)
            return CellValue.Text(value, kind);
        return CellValue.Text(value[..QueryLimits.MaxCellChars] + "…", kind, truncated: true);
    }

    /// <summary>Same rendering path the Results pane uses (intact rows + truncation marker).</summary>
    public static string RenderForDisplay(QueryResult result)
        => QueryOutcomeFormatter.FormatResults(result);

    /// <summary>Result-set body without status chrome (used by <see cref="QueryOutcomeFormatter"/>).</summary>
    public static string FormatResultSets(QueryResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Server: {result.Server}  Database: {result.Database}");
        sb.AppendLine($"Target: {result.TargetConfidence}");
        sb.AppendLine($"Partial: {result.PartialResults}");
        if (result.RetentionLimitReached)
            sb.AppendLine("RetentionLimitReached: true");
        if (result.Completion == QueryCompletionKind.FinalCompletionUnobserved)
            sb.AppendLine("FinalCompletionUnobserved: true");
        if (result.ResultSets.Count == 0)
        {
            sb.AppendLine("(no result sets)");
            return sb.ToString();
        }
        for (var i = 0; i < result.ResultSets.Count; i++)
        {
            var set = result.ResultSets[i];
            if (i > 0)
                sb.AppendLine();
            sb.AppendLine($"--- result set {i + 1} ---");
            sb.AppendLine(string.Join('\t', set.Columns));
            foreach (var row in set.Rows)
            {
                sb.AppendLine(string.Join('\t', row.Select(c =>
                {
                    var text = c.IsNull ? "NULL" : c.Display.Replace('\t', ' ').Replace('\n', ' ');
                    return c.Truncated ? text + " [truncated]" : text;
                })));
            }
            if (set.BudgetExceeded)
                sb.AppendLine("… [truncated: aggregate retained-output budget exceeded]");
            else if (set.Truncated)
                sb.AppendLine($"… [truncated: row limit or read stopped]");
        }
        return sb.ToString();
    }
}
