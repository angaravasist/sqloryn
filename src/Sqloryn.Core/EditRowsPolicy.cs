namespace Sqloryn.Core;

public sealed record EditColumnInfo(string Name, string SqlType, bool IsNullable, bool IsIdentity, bool IsComputed);

/// <summary>Outcome of <see cref="EditRowsPolicy.Evaluate"/>.</summary>
/// <param name="ConcurrencyColumns">
/// Columns whose loaded values must match on the server for a write to apply (besides the key).
/// A single rowversion column when the table has one; otherwise every non-key, non-computed column.
/// </param>
/// <param name="CharacterConcurrencyColumns">
/// The subset of <paramref name="ConcurrencyColumns"/> with char/varchar/nchar/nvarchar types. Pass these to
/// <see cref="EditRowsSql.BuildUpdate"/>/<see cref="EditRowsSql.BuildDelete"/> so they are compared with a binary collation.
/// </param>
public sealed record EditSupport(
    bool IsSupported,
    string? RefusalReason,
    IReadOnlyList<string> ConcurrencyColumns,
    bool UsesRowVersion,
    IReadOnlyList<string>? CharacterConcurrencyColumns = null)
{
    public static EditSupport Refuse(string reason) => new(false, reason, [], false);
}

/// <summary>Outcome of <see cref="EditRowsPolicy.EvaluateInsert"/> / <see cref="EditRowsPolicy.EvaluateDelete"/>.</summary>
/// <param name="WritableColumns">For inserts: columns the user may supply (identity, computed, rowversion and uneditable types are excluded).</param>
public sealed record EditOperationSupport(bool IsSupported, string? RefusalReason, IReadOnlyList<string> WritableColumns)
{
    public static EditOperationSupport Refuse(string reason) => new(false, reason, []);
}

/// <summary>
/// Decides whether a table can be edited safely. An edit set is refused unless the row can be identified by a
/// supported key and a dependable concurrency check exists (rowversion, or every non-key column comparable).
/// </summary>
/// <remarks>
/// Collation policy: under the default case-insensitive (and padding-insensitive) SQL collations, <c>col = @p</c> treats
/// 'abc', 'ABC' and 'abc ' as equal, so a concurrent edit that only changes case or trailing spaces would be silently
/// overwritten. Character columns (char, varchar, nchar, nvarchar) that take part in the predicate are therefore compared
/// as <c>nvarchar(max)</c> with a trailing sentinel under <see cref="CharacterConcurrencyCollation"/> (BIN2), so any
/// character difference is a conflict. A rowversion column, when present, remains the sole concurrency token and avoids
/// this cost. Key columns are not rewritten (that would defeat index seeks); they identify the row rather than guard it.
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

    static readonly HashSet<string> Character = new(StringComparer.OrdinalIgnoreCase) { "char", "varchar", "nchar", "nvarchar" };

    public const string CharacterConcurrencyCollation = EditRowsSql.CharacterConcurrencyCollation;

    public static bool IsCharacterType(string sqlType) => Character.Contains(sqlType);
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
        var characters = new List<string>();
        var unsupported = new List<string>();
        foreach (var column in columns)
        {
            if (keySet.Contains(column.Name) || column.IsComputed)
                continue;
            if (IsComparableType(column.SqlType))
            {
                concurrency.Add(column.Name);
                if (IsCharacterType(column.SqlType))
                    characters.Add(column.Name);
            }
            else
                unsupported.Add($"{column.Name} ({column.SqlType})");
        }

        if (unsupported.Count > 0)
            return EditSupport.Refuse(
                "No dependable concurrency check: the table has no rowversion column and these columns cannot be compared exactly: " +
                string.Join(", ", unsupported) + ".");
        return new EditSupport(true, null, concurrency, false, characters);
    }

    /// <summary>
    /// Insert is allowed only where updates are (identifiable key, no INSTEAD OF trigger), and only when every column the
    /// user cannot supply (identity, computed, rowversion) is server-generated and every other required column has a
    /// type the grid can write. Column defaults are not visible to the policy, so a NOT NULL column of an uneditable
    /// type is refused rather than guessed at; a NOT NULL column with no default still fails on the server and rolls back.
    /// </summary>
    public static EditOperationSupport EvaluateInsert(EditSupport rowSupport, IReadOnlyList<EditColumnInfo> columns)
    {
        ArgumentNullException.ThrowIfNull(rowSupport);
        ArgumentNullException.ThrowIfNull(columns);
        if (!rowSupport.IsSupported)
            return EditOperationSupport.Refuse(rowSupport.RefusalReason ?? "Rows cannot be inserted into this table safely.");

        var writable = new List<string>();
        var blocked = new List<string>();
        foreach (var column in columns)
        {
            if (column.IsIdentity || column.IsComputed || IsRowVersionType(column.SqlType))
                continue;
            if (IsEditableType(column.SqlType))
                writable.Add(column.Name);
            else if (!column.IsNullable)
                blocked.Add($"{column.Name} ({column.SqlType})");
        }
        if (blocked.Count > 0)
            return EditOperationSupport.Refuse(
                "Insert is disabled: these required columns have types this grid cannot write: " + string.Join(", ", blocked) + ".");
        return new EditOperationSupport(true, null, writable);
    }

    /// <summary>
    /// Delete needs the same guarantees as update: the key must identify one row and a dependable concurrency check must
    /// exist, so a row changed by someone else is a conflict rather than a silent delete. The count only reflects the
    /// outer statement; cascading foreign keys can remove further rows, which the confirmation dialog must say.
    /// </summary>
    public static EditOperationSupport EvaluateDelete(EditSupport rowSupport)
    {
        ArgumentNullException.ThrowIfNull(rowSupport);
        return rowSupport.IsSupported
            ? new EditOperationSupport(true, null, [])
            : EditOperationSupport.Refuse(rowSupport.RefusalReason ?? "Rows cannot be deleted from this table safely.");
    }
}
