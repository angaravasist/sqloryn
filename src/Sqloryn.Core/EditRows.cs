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
/// Parameterised statement. The final result set is a single int: the number of rows affected.
/// Pass it to <see cref="EditRowsSql.EnsureSingleRowAffected"/>; zero rows means an optimistic-concurrency conflict.
/// </summary>
public sealed record EditCommand(string CommandText, IReadOnlyList<EditParameter> Parameters);

public static class EditRowsSql
{
    public const int MaxTopRows = 1_000_000;

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
    /// </summary>
    public static EditCommand BuildUpdate(
        TableRef table,
        RowKey key,
        IReadOnlyDictionary<string, object?> newValues,
        IReadOnlyDictionary<string, object?>? originalValues = null,
        IReadOnlySet<string>? excludeFromConcurrency = null)
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
            var name = "@n" + index.ToString(CultureInfo.InvariantCulture);
            if (index > 0)
                sb.Append(", ");
            sb.Append(SqlIdentity.QuoteIdentifier(column)).Append(" = ").Append(name);
            parameters.Add(new EditParameter(name, value));
            index++;
        }

        AppendWhere(sb, parameters, key, originalValues, excludeFromConcurrency);
        sb.Append(";\nSELECT @@ROWCOUNT;\n");
        return new EditCommand(sb.ToString(), parameters);
    }

    public static EditCommand BuildDelete(
        TableRef table,
        RowKey key,
        IReadOnlyDictionary<string, object?>? originalValues = null,
        IReadOnlySet<string>? excludeFromConcurrency = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(key);
        var parameters = new List<EditParameter>();
        var sb = new StringBuilder();
        sb.Append("SET NOCOUNT ON;").Append('\n');
        sb.Append("DELETE FROM ").Append(table.Qualified);
        AppendWhere(sb, parameters, key, originalValues, excludeFromConcurrency);
        sb.Append(";\nSELECT @@ROWCOUNT;\n");
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
        IReadOnlySet<string>? excludeFromConcurrency)
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
            sb.Append(" AND ").Append(SqlIdentity.QuoteIdentifier(column));
            if (value is null or DBNull)
            {
                sb.Append(" IS NULL");
                continue;
            }
            var name = "@o" + index.ToString(CultureInfo.InvariantCulture);
            sb.Append(" = ").Append(name);
            parameters.Add(new EditParameter(name, value));
            index++;
        }
    }
}
