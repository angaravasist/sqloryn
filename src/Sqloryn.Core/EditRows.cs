using System.Globalization;
using System.Text;

namespace Sqloryn.Core;

public sealed record TableRef(string Database, string Schema, string Name)
{
    public string Qualified => SqlIdentity.Qualify3(Database, Schema, Name);
}

/// <summary>Identifies one row by its key columns. Key values may not be null.</summary>
public sealed class RowKey
{
    public RowKey(IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(values);
        if (columns.Count == 0)
            throw new ArgumentException("A row key needs at least one column.");
        if (columns.Count != values.Count)
            throw new ArgumentException("Key column and value counts differ.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            SqlIdentity.QuoteIdentifier(columns[i]);
            if (!seen.Add(columns[i]))
                throw new ArgumentException($"Duplicate key column: {columns[i]}");
            if (values[i] is null or DBNull)
                throw new ArgumentException($"Key column {columns[i]} cannot be null.");
            if (RawCellOmitted.IsOmitted(values[i]))
                throw new ArgumentException($"Key column {columns[i]} was not fully loaded and cannot identify a row.");
        }
        Columns = columns;
        Values = values;
    }

    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<object?> Values { get; }
}

public sealed class EditRowsConflictException : Exception
{
    public EditRowsConflictException()
        : base("The row was changed or deleted by someone else, or no longer matches. Reload the data and try again.")
    {
    }

    public EditRowsConflictException(string message) : base(message)
    {
    }
}

public sealed record EditParameter(string Name, object? Value);

/// <summary>
/// Parameterised statement. It emits a result set whose single int column is named <see cref="EditRowsSql.RowCountColumn"/>,
/// holding <c>@@ROWCOUNT</c> read immediately after the write. Other result sets (trigger output) are ignored; see
/// <see cref="EditRowsSql.ReadAffectedCountAsync"/>. Zero rows means an optimistic-concurrency conflict.
/// </summary>
public sealed record EditCommand(string CommandText, IReadOnlyList<EditParameter> Parameters);

public static class EditRowsSql
{
    public const int MaxTopRows = 1_000_000;

    /// <summary>Binary collation applied to character columns in optimistic-concurrency predicates (see <see cref="EditRowsPolicy"/>).</summary>
    public const string CharacterConcurrencyCollation = "Latin1_General_BIN2";

    /// <summary>Column name that marks the row-count result set so trigger result sets cannot be mistaken for it.</summary>
    public const string RowCountColumn = "__sqloryn_rowcount";

    /// <summary>
    /// Reads every result set and returns the single marked row count. Result sets that triggers emit are skipped
    /// (not read row by row) but the reader is always advanced to the end so errors raised later still surface.
    /// Throws <see cref="InvalidOperationException"/> when the marked set is missing, repeated, or malformed.
    /// </summary>
    public static async Task<int> ReadAffectedCountAsync(
        System.Data.Common.DbDataReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        int? count = null;
        var seen = 0;
        do
        {
            if (reader.FieldCount != 1 || !string.Equals(reader.GetName(0), RowCountColumn, StringComparison.Ordinal))
                continue;
            seen++;
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
                throw new InvalidOperationException("The server did not report an affected row count.");
            count = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        if (seen != 1 || count is null)
            throw new InvalidOperationException(
                seen == 0
                    ? "The server did not report an affected row count."
                    : "The server reported more than one affected row count; refusing to trust it.");
        return count.Value;
    }

    public static string BuildSelectTop(TableRef table, int topN)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (topN is < 1 or > MaxTopRows)
            throw new ArgumentOutOfRangeException(nameof(topN), $"topN must be 1-{MaxTopRows}.");
        return $"SELECT TOP ({topN.ToString(CultureInfo.InvariantCulture)}) * FROM {table.Qualified};{Environment.NewLine}";
    }

    /// <summary>
    /// UPDATE keyed by <paramref name="key"/>. When <paramref name="originalValues"/> is given, every non-key original value
    /// is added to the WHERE clause (NULL as IS NULL) so a concurrent change yields zero rows. Pass columns whose type
    /// cannot be compared (text, ntext, image, xml, spatial) in <paramref name="excludeFromConcurrency"/>.
    /// The key must identify exactly one row (for example the primary key).
    /// Columns named in <paramref name="characterColumns"/> (char/varchar/nchar/nvarchar) are compared under
    /// <see cref="CharacterConcurrencyCollation"/> so case and trailing-space changes count as conflicts.
    /// </summary>
    public static EditCommand BuildUpdate(
        TableRef table,
        RowKey key,
        IReadOnlyDictionary<string, object?> newValues,
        IReadOnlyDictionary<string, object?>? originalValues = null,
        IReadOnlySet<string>? excludeFromConcurrency = null,
        IReadOnlySet<string>? characterColumns = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(newValues);
        if (newValues.Count == 0)
            throw new ArgumentException("No values to update.");

        var parameters = new List<EditParameter>();
        var sb = new StringBuilder();
        sb.Append("SET NOCOUNT ON;").Append('\n');
        sb.Append("UPDATE ").Append(table.Qualified).Append(" SET ");
        var index = 0;
        foreach (var (column, value) in newValues)
        {
            if (key.Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Key column {column} cannot be updated.");
            if (RawCellOmitted.IsOmitted(value))
                throw new ArgumentException($"Column {column} holds a value that was not fully loaded and cannot be written back.");
            var name = "@n" + index.ToString(CultureInfo.InvariantCulture);
            if (index > 0)
                sb.Append(", ");
            sb.Append(SqlIdentity.QuoteIdentifier(column)).Append(" = ").Append(name);
            parameters.Add(new EditParameter(name, value));
            index++;
        }

        AppendWhere(sb, parameters, key, originalValues, excludeFromConcurrency, characterColumns);
        sb.Append(";\nSELECT @@ROWCOUNT AS [").Append(RowCountColumn).Append("];\n");
        return new EditCommand(sb.ToString(), parameters);
    }

    public static EditCommand BuildDelete(
        TableRef table,
        RowKey key,
        IReadOnlyDictionary<string, object?>? originalValues = null,
        IReadOnlySet<string>? excludeFromConcurrency = null,
        IReadOnlySet<string>? characterColumns = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(key);
        var parameters = new List<EditParameter>();
        var sb = new StringBuilder();
        sb.Append("SET NOCOUNT ON;").Append('\n');
        sb.Append("DELETE FROM ").Append(table.Qualified);
        AppendWhere(sb, parameters, key, originalValues, excludeFromConcurrency, characterColumns);
        sb.Append(";\nSELECT @@ROWCOUNT AS [").Append(RowCountColumn).Append("];\n");
        return new EditCommand(sb.ToString(), parameters);
    }

    public static void EnsureSingleRowAffected(int rowsAffected)
    {
        if (rowsAffected == 0)
            throw new EditRowsConflictException();
        if (rowsAffected != 1)
            throw new InvalidOperationException(
                $"The edit affected {rowsAffected.ToString(CultureInfo.InvariantCulture)} rows; the key did not identify a single row.");
    }

    static void AppendWhere(
        StringBuilder sb,
        List<EditParameter> parameters,
        RowKey key,
        IReadOnlyDictionary<string, object?>? originalValues,
        IReadOnlySet<string>? excludeFromConcurrency,
        IReadOnlySet<string>? characterColumns)
    {
        sb.Append(" WHERE ");
        for (var i = 0; i < key.Columns.Count; i++)
        {
            var name = "@k" + i.ToString(CultureInfo.InvariantCulture);
            if (i > 0)
                sb.Append(" AND ");
            sb.Append(SqlIdentity.QuoteIdentifier(key.Columns[i])).Append(" = ").Append(name);
            parameters.Add(new EditParameter(name, key.Values[i]));
        }

        if (originalValues is null)
            return;
        var index = 0;
        foreach (var (column, value) in originalValues)
        {
            if (key.Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                continue;
            if (excludeFromConcurrency is not null && excludeFromConcurrency.Contains(column))
                continue;
            if (RawCellOmitted.IsOmitted(value))
                throw new ArgumentException(
                    $"Column {column} was not fully loaded, so it cannot be used for the concurrency check. Exclude the column or refuse the edit.");
            sb.Append(" AND ");
            var quoted = SqlIdentity.QuoteIdentifier(column);
            if (value is null or DBNull)
            {
                sb.Append(quoted).Append(" IS NULL");
                continue;
            }
            var name = "@o" + index.ToString(CultureInfo.InvariantCulture);
            if (characterColumns is not null && characterColumns.Contains(column))
            {
                // '=' under the column's (usually case/accent-insensitive) collation hides 'abc' -> 'ABC', and ANSI
                // padding rules hide trailing-space changes even under a binary collation. Compare as nvarchar(max)
                // (lossless for char/varchar) with a sentinel so trailing spaces count, under a BIN2 collation.
                sb.Append("(CAST(").Append(quoted).Append(" AS nvarchar(max)) + N'|') COLLATE ")
                    .Append(CharacterConcurrencyCollation)
                    .Append(" = (CAST(").Append(name).Append(" AS nvarchar(max)) + N'|')");
            }
            else
            {
                sb.Append(quoted).Append(" = ").Append(name);
            }
            parameters.Add(new EditParameter(name, value));
            index++;
        }
    }
}
