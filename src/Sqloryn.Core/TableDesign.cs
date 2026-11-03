using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sqloryn.Core;

public sealed record ColumnDefinition
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public bool IsNullable { get; init; } = true;
    public bool IsIdentity { get; init; }
    /// <summary>Character/byte length for char-like types; -1 means MAX. Overrides a length embedded in <see cref="DataType"/>.</summary>
    public int? MaxLength { get; init; }
}

public sealed record TableDesignDraft
{
    public required string Database { get; init; }
    public required string Schema { get; init; }
    public required string TableName { get; init; }
    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }
}

/// <summary>Generates DDL text for review only. Nothing here executes SQL.</summary>
public static partial class TableDesignSql
{
    const int MaxColumns = 1024;

    static readonly HashSet<string> LengthTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "char", "varchar", "nchar", "nvarchar", "binary", "varbinary"
    };

    static readonly HashSet<string> ScaleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "decimal", "numeric"
    };

    static readonly HashSet<string> TimePrecisionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "datetime2", "datetimeoffset", "time"
    };

    static readonly HashSet<string> SimpleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bigint", "int", "smallint", "tinyint", "bit", "money", "smallmoney", "float", "real",
        "date", "datetime", "smalldatetime", "uniqueidentifier", "text", "ntext", "image", "xml", "sql_variant"
    };

    static readonly HashSet<string> IdentityTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bigint", "int", "smallint", "tinyint", "decimal", "numeric"
    };

    [GeneratedRegex(@"\A\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:\(\s*(max|\d{1,5})\s*(?:,\s*(\d{1,3}))?\s*\))?\s*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DataTypePattern();

    public static string PreviewCreate(TableDesignDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(SqlIdentity.Qualify3(draft.Database, draft.Schema, draft.TableName)).Append(" (\n");
        for (var i = 0; i < draft.Columns.Count; i++)
        {
            sb.Append("    ").Append(ColumnClause(draft.Columns[i]));
            sb.Append(i < draft.Columns.Count - 1 ? ",\n" : "\n");
        }
        sb.Append(");\n");
        return sb.ToString();
    }

    public static string PreviewAlter(TableDesignDraft existing, TableDesignDraft desired)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(desired);
        ValidateDraft(existing);
        ValidateDraft(desired);
        if (!SameTable(existing, desired))
            throw new ArgumentException("Renaming or moving a table is not supported by the design preview.");

        var table = SqlIdentity.Qualify3(desired.Database, desired.Schema, desired.TableName);
        var before = existing.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var after = desired.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        foreach (var column in existing.Columns.Where(c => !after.ContainsKey(c.Name)))
        {
            sb.Append("-- DESTRUCTIVE: permanently removes column ").Append(SqlIdentity.QuoteIdentifier(column.Name)).Append(" and its data.\n");
            sb.Append("ALTER TABLE ").Append(table).Append(" DROP COLUMN ").Append(SqlIdentity.QuoteIdentifier(column.Name)).Append(";\n");
        }

        foreach (var column in desired.Columns)
        {
            if (!before.TryGetValue(column.Name, out var old))
            {
                if (!column.IsNullable)
                    sb.Append("-- NOTE: adding a NOT NULL column fails on a non-empty table unless a default is supplied.\n");
                sb.Append("ALTER TABLE ").Append(table).Append(" ADD ").Append(ColumnClause(column)).Append(";\n");
                continue;
            }

            if (old.IsIdentity != column.IsIdentity)
                throw new NotSupportedException(
                    $"Changing IDENTITY on column {column.Name} requires rebuilding the table and is not generated.");

            var oldType = TypeSql(old);
            var newType = TypeSql(column);
            if (oldType == newType && old.IsNullable == column.IsNullable)
                continue;
            if (!string.Equals(old.Name, column.Name, StringComparison.Ordinal))
                throw new ArgumentException("Column names differ only by case; renames are not supported.");
            sb.Append("-- NOTE: altering a column type can fail or lose data if existing values do not fit.\n");
            sb.Append("ALTER TABLE ").Append(table).Append(" ALTER COLUMN ")
                .Append(SqlIdentity.QuoteIdentifier(column.Name)).Append(' ').Append(newType)
                .Append(column.IsNullable ? " NULL" : " NOT NULL").Append(";\n");
        }

        return sb.Length == 0 ? "-- No changes.\n" : sb.ToString();
    }

    public static bool HasDestructiveChanges(TableDesignDraft existing, TableDesignDraft desired)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(desired);
        var after = desired.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var before = existing.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        if (existing.Columns.Any(c => !after.Contains(c.Name)))
            return true;
        return desired.Columns.Any(c => before.TryGetValue(c.Name, out var old) && TypeSql(old) != TypeSql(c));
    }

    static bool SameTable(TableDesignDraft a, TableDesignDraft b)
        => string.Equals(a.Database, b.Database, StringComparison.Ordinal) &&
           string.Equals(a.Schema, b.Schema, StringComparison.Ordinal) &&
           string.Equals(a.TableName, b.TableName, StringComparison.Ordinal);

    static void ValidateDraft(TableDesignDraft draft)
    {
        SqlIdentity.QuoteIdentifier(draft.Database);
        SqlIdentity.QuoteIdentifier(draft.Schema);
        SqlIdentity.QuoteIdentifier(draft.TableName);
        if (draft.Columns.Count == 0)
            throw new ArgumentException("A table needs at least one column.");
        if (draft.Columns.Count > MaxColumns)
            throw new ArgumentException($"A table cannot have more than {MaxColumns} columns.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identityCount = 0;
        foreach (var column in draft.Columns)
        {
            SqlIdentity.QuoteIdentifier(column.Name);
            if (!seen.Add(column.Name))
                throw new ArgumentException($"Duplicate column name: {column.Name}");
            TypeSql(column);
            if (column.IsIdentity)
            {
                identityCount++;
                if (column.IsNullable)
                    throw new ArgumentException($"Identity column {column.Name} cannot be nullable.");
            }
        }
        if (identityCount > 1)
            throw new ArgumentException("A table can have only one identity column.");
    }

    static string ColumnClause(ColumnDefinition column)
    {
        var sb = new StringBuilder();
        sb.Append(SqlIdentity.QuoteIdentifier(column.Name)).Append(' ').Append(TypeSql(column));
        if (column.IsIdentity)
            sb.Append(" IDENTITY(1,1)");
        sb.Append(column.IsNullable ? " NULL" : " NOT NULL");
        return sb.ToString();
    }

    static string TypeSql(ColumnDefinition column)
    {
        var match = DataTypePattern().Match(column.DataType ?? "");
        if (!match.Success)
            throw new ArgumentException($"Unsupported data type for column {column.Name}.");
        var name = match.Groups[1].Value.ToLowerInvariant();
        var arg1 = match.Groups[2].Success ? match.Groups[2].Value : null;
        var arg2 = match.Groups[3].Success ? match.Groups[3].Value : null;

        if (column.IsIdentity && !IdentityTypes.Contains(name))
            throw new ArgumentException($"Column {column.Name} cannot be an identity column with type {name}.");

        if (name == "float" && arg1 is not null)
        {
            if (arg2 is not null || !int.TryParse(arg1, NumberStyles.None, CultureInfo.InvariantCulture, out var bits) || bits is < 1 or > 53)
                throw new ArgumentException($"Invalid float precision for column {column.Name}.");
            return $"float({bits.ToString(CultureInfo.InvariantCulture)})";
        }

        if (SimpleTypes.Contains(name))
        {
            if (arg1 is not null)
                throw new ArgumentException($"Type {name} does not take arguments (column {column.Name}).");
            return name;
        }

        if (LengthTypes.Contains(name))
        {
            var length = column.MaxLength?.ToString(CultureInfo.InvariantCulture) ?? arg1;
            if (arg2 is not null || length is null)
                throw new ArgumentException($"A length is required for {name} column {column.Name}.");
            if (column.MaxLength is -1 || string.Equals(length, "max", StringComparison.OrdinalIgnoreCase))
                return $"{name}(MAX)";
            var value = int.Parse(length, CultureInfo.InvariantCulture);
            var limit = name is "nchar" or "nvarchar" ? 4000 : 8000;
            if (value < 1 || value > limit)
                throw new ArgumentException($"Length for column {column.Name} must be 1-{limit} or MAX.");
            return $"{name}({value.ToString(CultureInfo.InvariantCulture)})";
        }

        if (ScaleTypes.Contains(name))
        {
            if (arg1 is null || string.Equals(arg1, "max", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Precision is required for {name} column {column.Name}, e.g. {name}(18,2).");
            var precision = int.Parse(arg1, CultureInfo.InvariantCulture);
            var scale = arg2 is null ? 0 : int.Parse(arg2, CultureInfo.InvariantCulture);
            if (precision is < 1 or > 38 || scale < 0 || scale > precision)
                throw new ArgumentException($"Invalid precision/scale for column {column.Name}.");
            return $"{name}({precision.ToString(CultureInfo.InvariantCulture)},{scale.ToString(CultureInfo.InvariantCulture)})";
        }

        if (TimePrecisionTypes.Contains(name))
        {
            if (arg1 is null)
                return name;
            if (arg2 is not null || !int.TryParse(arg1, NumberStyles.None, CultureInfo.InvariantCulture, out var precision) || precision > 7)
                throw new ArgumentException($"Invalid fractional seconds precision for column {column.Name}.");
            return $"{name}({precision.ToString(CultureInfo.InvariantCulture)})";
        }

        throw new ArgumentException($"Unsupported data type '{name}' for column {column.Name}.");
    }
}
