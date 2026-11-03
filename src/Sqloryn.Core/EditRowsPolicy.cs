namespace Sqloryn.Core;

public sealed record EditColumnInfo(string Name, string SqlType, bool IsNullable, bool IsIdentity, bool IsComputed);

/// <summary>Outcome of <see cref="EditRowsPolicy.Evaluate"/>.</summary>
/// <param name="ConcurrencyColumns">
/// Columns whose loaded values must match on the server for a write to apply (besides the key).
/// A single rowversion column when the table has one; otherwise every non-key, non-computed column.
/// </param>
public sealed record EditSupport(bool IsSupported, string? RefusalReason, IReadOnlyList<string> ConcurrencyColumns, bool UsesRowVersion)
{
    public static EditSupport Refuse(string reason) => new(false, reason, [], false);
}

/// <summary>
/// Decides whether a table can be edited safely. An edit set is refused unless the row can be identified by a
/// supported key and a dependable concurrency check exists (rowversion, or every non-key column comparable).
/// </summary>
/// <remarks>
/// Trigger risk: <c>@@ROWCOUNT</c> after an UPDATE reflects the outer statement for AFTER triggers, and trigger result
/// sets are drained (see <see cref="EditRowsSql.ReadAffectedCountAsync"/>), but an INSTEAD OF trigger replaces the write
/// and makes the count unreliable, so such tables are refused. AFTER triggers still run inside the owned transaction:
/// a trigger that rolls back, or raises an error, rolls the whole set back or surfaces as an unknown outcome.
/// </remarks>
public static class EditRowsPolicy
{
    // Types whose values round-trip exactly through the CLR parameter we send, so '=' is a faithful "unchanged" test.
    static readonly HashSet<string> Comparable = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit", "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "money", "smallmoney", "float", "real",
        "char", "varchar", "nchar", "nvarchar", "date", "datetime", "smalldatetime", "uniqueidentifier",
        "binary", "varbinary"
    };

    static readonly HashSet<string> Editable = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit", "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "money", "smallmoney", "float", "real",
        "char", "varchar", "nchar", "nvarchar", "date", "datetime", "smalldatetime", "uniqueidentifier"
    };

    public static bool IsComparableType(string sqlType) => Comparable.Contains(sqlType);
    public static bool IsEditableType(string sqlType) => Editable.Contains(sqlType);
    public static bool IsRowVersionType(string sqlType) => sqlType.Equals("timestamp", StringComparison.OrdinalIgnoreCase)
                                                           || sqlType.Equals("rowversion", StringComparison.OrdinalIgnoreCase);

    public static EditSupport Evaluate(
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<EditColumnInfo> columns,
        bool hasInsteadOfTrigger)
    {
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(columns);

        if (keyColumns.Count == 0)
            return EditSupport.Refuse("This table has no primary key, so rows cannot be identified safely.");
        if (hasInsteadOfTrigger)
            return EditSupport.Refuse(
                "This table has an enabled INSTEAD OF trigger, which replaces the write and makes affected-row counts unreliable.");

        var byName = columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keyColumns)
        {
            if (!byName.TryGetValue(key, out var keyColumn))
                return EditSupport.Refuse($"Key column '{key}' is missing from the loaded columns.");
            if (!IsComparableType(keyColumn.SqlType))
                return EditSupport.Refuse($"Key column '{key}' has type {keyColumn.SqlType}, which cannot be matched exactly.");
        }

        var keySet = new HashSet<string>(keyColumns, StringComparer.OrdinalIgnoreCase);
        var rowVersions = columns.Where(c => IsRowVersionType(c.SqlType)).ToList();
        if (rowVersions.Count == 1)
            return new EditSupport(true, null, [rowVersions[0].Name], true);

        var concurrency = new List<string>();
        var unsupported = new List<string>();
        foreach (var column in columns)
        {
            if (keySet.Contains(column.Name) || column.IsComputed)
                continue;
            if (IsComparableType(column.SqlType))
                concurrency.Add(column.Name);
            else
                unsupported.Add($"{column.Name} ({column.SqlType})");
        }

        if (unsupported.Count > 0)
            return EditSupport.Refuse(
                "No dependable concurrency check: the table has no rowversion column and these columns cannot be compared exactly: " +
                string.Join(", ", unsupported) + ".");
        return new EditSupport(true, null, concurrency, false);
    }
}
