using System.Globalization;
using System.Text;

namespace Sqloryn.Core;

public enum CsvImportMode
{
    /// <summary>Insert into an existing table; existing rows are kept.</summary>
    Append,

    /// <summary>Create a new table (refused by the server when the name is taken) and insert into it.</summary>
    Create,

    /// <summary>DELETE every row of an existing table, then insert. Destructive; atomic with the inserts.</summary>
    Replace
}

/// <param name="SourceIndex">0-based source column.</param>
/// <param name="TargetColumn">Destination column name (existing column for Append/Replace, new column for Create).</param>
/// <param name="Type">How cells are parsed and sent. For Create it is also the column type.</param>
/// <param name="Length">Create only: nvarchar length (null = MAX).</param>
/// <param name="Scale">Create only: decimal scale.</param>
public sealed record CsvColumnMapping(int SourceIndex, string TargetColumn, CsvSqlType Type, int? Length = null, int? Scale = null);

public sealed class CsvImportConversionException(int rowInBatch, int sourceColumn, string message) : Exception(message)
{
    public int RowInBatch { get; } = rowInBatch;
    public int SourceColumn { get; } = sourceColumn;
}

/// <summary>
/// Source-to-target mapping of one flat-file import plus every piece of SQL it will run. All SQL for the importer is
/// generated here: identifiers are quoted, every cell is a parameter. <see cref="GeneratePreviewSql"/> is pure and
/// shows what apply would do; nothing here touches a database or a file.
/// </summary>
public sealed class CsvImportPlan
{
    public const int MaxTargetColumns = 1024;
    const int MaxParametersPerBatch = 2000;
    const int MaxRowsPerBatch = 1000;

    public CsvImportPlan(
        TableRef target,
        CsvImportMode mode,
        IReadOnlyList<CsvColumnMapping> mappings,
        IReadOnlyList<string>? keyColumns,
        bool emptyTextAsNull)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(mappings);
        Target = target;
        Mode = mode;
        Mappings = mappings;
        KeyColumns = keyColumns ?? [];
        EmptyTextAsNull = emptyTextAsNull;
    }

    public TableRef Target { get; }
    public CsvImportMode Mode { get; }
    public IReadOnlyList<CsvColumnMapping> Mappings { get; }

    /// <summary>Target columns that must be unique across the file. Duplicates are refused before anything is written.</summary>
    public IReadOnlyList<string> KeyColumns { get; }

    public bool EmptyTextAsNull { get; }

    /// <summary>Replace removes existing rows.</summary>
    public bool IsDestructive => Mode == CsvImportMode.Replace;

    /// <summary>Rows sent per INSERT, kept under SQL Server's 2,100-parameter and 1,000-row-constructor limits.</summary>
    public int RowsPerBatch => Math.Clamp(MaxParametersPerBatch / Math.Max(1, Mappings.Count), 1, MaxRowsPerBatch);

    /// <summary>Default mapping from a preview: every column, guessed types, and text widths only when the whole file was scanned.</summary>
    public static CsvImportPlan FromPreview(
        CsvImportPreviewResult preview,
        TableRef target,
        CsvImportMode mode,
        IReadOnlyList<string>? keyColumns = null,
        bool emptyTextAsNull = false)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var mappings = preview.Columns.Select(c => new CsvColumnMapping(
            c.Index,
            c.Name,
            c.GuessedType,
            c.GuessedType == CsvSqlType.NVarChar ? SuggestTextLength(c.MaxLength, preview.ScanTruncated) : null,
            c.GuessedType == CsvSqlType.Decimal ? c.MaxScale : null)).ToList();
        return new CsvImportPlan(target, mode, mappings, keyColumns, emptyTextAsNull);
    }

    /// <summary>nvarchar width for a new text column: a bucket over the longest scanned cell, or null (MAX) when the scan did not cover the file.</summary>
    public static int? SuggestTextLength(int observed, bool scanTruncated)
    {
        if (scanTruncated)
            return null;
        foreach (var size in new[] { 50, 100, 255, 500, 1000, 4000 })
            if (observed <= size)
                return size;
        return null;
    }

    /// <summary>Returns every reason the plan cannot run (errors) plus advisories (warnings). Empty means valid.</summary>
    public IReadOnlyList<CsvImportIssue> Validate(int sourceColumnCount)
    {
        var issues = new List<CsvImportIssue>();
        void Error(string message, int? column = null) => issues.Add(new CsvImportIssue(CsvIssueSeverity.Error, null, null, column, message));

        foreach (var (label, part) in new[] { ("database", Target.Database), ("schema", Target.Schema), ("table", Target.Name) })
        {
            if (!IsValidIdentifier(part))
                Error($"The destination {label} name is missing or not a valid identifier.");
        }

        if (Mappings.Count == 0)
            Error("Map at least one source column to a destination column.");
        if (Mappings.Count > MaxTargetColumns)
            Error($"A table can have at most {MaxTargetColumns} columns.");

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<int>();
        foreach (var m in Mappings)
        {
            if (m.SourceIndex < 0 || m.SourceIndex >= sourceColumnCount)
                Error($"Source column {m.SourceIndex + 1} does not exist in the file.", m.SourceIndex);
            else if (!sources.Add(m.SourceIndex))
                Error($"Source column {m.SourceIndex + 1} is mapped more than once.", m.SourceIndex);

            if (!IsValidIdentifier(m.TargetColumn))
                Error($"The destination column name for source column {m.SourceIndex + 1} is missing or not a valid identifier.", m.SourceIndex);
            else if (!targets.Add(m.TargetColumn.Trim()))
                Error($"Destination column '{Printable(m.TargetColumn)}' is mapped more than once.", m.SourceIndex);
        }

        foreach (var key in KeyColumns)
        {
            if (!targets.Contains(key.Trim()))
                Error($"Key column '{Printable(key)}' is not one of the mapped destination columns.");
        }

        return issues;
    }

    static bool IsValidIdentifier(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && !name.Any(char.IsControl);

    static string Printable(string text)
    {
        var clean = new string(text.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
        return clean.Length <= 60 ? clean : clean[..60] + "…";
    }

    void EnsureValid()
    {
        var errors = Validate(int.MaxValue).Where(i => i.Severity == CsvIssueSeverity.Error).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException("The import plan is invalid: " + errors[0].Message);
    }

    string QuotedColumn(CsvColumnMapping m) => SqlIdentity.QuoteIdentifier(m.TargetColumn.Trim());

    /// <summary>CREATE TABLE text for <see cref="CsvImportMode.Create"/>; empty otherwise.</summary>
    public IReadOnlyList<string> GetPrepareStatements()
    {
        EnsureValid();
        if (Mode != CsvImportMode.Create)
            return [];
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(Target.Qualified).Append(" (");
        for (var i = 0; i < Mappings.Count; i++)
        {
            var m = Mappings[i];
            sb.Append(i == 0 ? "\n  " : ",\n  ")
              .Append(QuotedColumn(m)).Append(' ')
              .Append(CsvSqlTypes.ToSqlTypeText(m.Type, m.Length, m.Scale)).Append(" NULL");
        }
        sb.Append("\n);");
        return [sb.ToString()];
    }

    /// <summary>Counted DELETE for <see cref="CsvImportMode.Replace"/> (never TRUNCATE, so it rolls back with the inserts); empty otherwise.</summary>
    public IReadOnlyList<EditCommand> GetPrepareCommands()
    {
        EnsureValid();
        if (Mode != CsvImportMode.Replace)
            return [];
        return
        [
            new EditCommand(
                $"SET NOCOUNT ON;\nDELETE FROM {Target.Qualified};\nSELECT @@ROWCOUNT AS [{EditRowsSql.RowCountColumn}];\n",
                [])
        ];
    }

    string InsertText(int rowCount)
    {
        var sb = new StringBuilder();
        sb.Append("SET NOCOUNT ON;\nINSERT INTO ").Append(Target.Qualified).Append(" (");
        for (var i = 0; i < Mappings.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(QuotedColumn(Mappings[i]));
        }
        sb.Append(") VALUES ");
        var p = 0;
        for (var r = 0; r < rowCount; r++)
        {
            sb.Append(r == 0 ? "(" : ", (");
            for (var c = 0; c < Mappings.Count; c++)
            {
                if (c > 0) sb.Append(", ");
                var name = "@p" + p.ToString(CultureInfo.InvariantCulture);
                p++;
                sb.Append(Mappings[c].Type == CsvSqlType.DateTime2 ? $"CONVERT(datetime2(7), {name}, 126)" : name);
            }
            sb.Append(')');
        }
        sb.Append(";\nSELECT @@ROWCOUNT AS [").Append(EditRowsSql.RowCountColumn).Append("];\n");
        return sb.ToString();
    }

    /// <summary>
    /// One multi-row parameterised INSERT for <paramref name="rows"/> (cells in source-column order). Throws
    /// <see cref="CsvImportConversionException"/> when a cell cannot be converted; callers validate first.
    /// </summary>
    public EditCommand BuildInsert(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        EnsureValid();
        if (rows.Count == 0 || rows.Count > RowsPerBatch)
            throw new ArgumentException($"A batch holds 1-{RowsPerBatch} rows.", nameof(rows));

        var parameters = new List<EditParameter>(rows.Count * Mappings.Count);
        for (var r = 0; r < rows.Count; r++)
        {
            foreach (var m in Mappings)
            {
                if (m.SourceIndex >= rows[r].Count)
                    throw new CsvImportConversionException(r, m.SourceIndex, $"Row is missing source column {m.SourceIndex + 1}.");
                if (!TryConvert(rows[r][m.SourceIndex], m.Type, EmptyTextAsNull, out var value, out var error))
                    throw new CsvImportConversionException(r, m.SourceIndex, $"Value in source column {m.SourceIndex + 1} {error}.");
                parameters.Add(new EditParameter("@p" + parameters.Count.ToString(CultureInfo.InvariantCulture), value));
            }
        }
        return new EditCommand(InsertText(rows.Count), parameters);
    }

    /// <summary>Pure description of what apply runs. Contains no file values; safe to show and to copy into a query window.</summary>
    public string GeneratePreviewSql()
    {
        EnsureValid();
        var sb = new StringBuilder();
        sb.Append("-- SQLoryn flat-file import preview. Nothing has been executed.\n");
        sb.Append(Mode switch
        {
            CsvImportMode.Append => "-- Mode: append rows to an existing table. Existing rows are kept.\n",
            CsvImportMode.Create => "-- Mode: create a new table, then insert. Everything runs in one transaction.\n",
            _ => "-- Mode: REPLACE. Every existing row is deleted, then the file is inserted, in one transaction.\n"
        });
        foreach (var ddl in GetPrepareStatements())
            sb.Append(ddl).Append('\n');
        foreach (var command in GetPrepareCommands())
            sb.Append(StripRowCountMarker(command.CommandText)).Append('\n');
        sb.Append($"-- Rows are inserted in batches of up to {RowsPerBatch} using parameters, shown here for one row:\n");
        sb.Append(StripRowCountMarker(InsertText(1))).Append('\n');
        sb.Append("-- Any failure, duplicate key or cancel rolls the whole import back.\n");
        return sb.ToString();
    }

    static string StripRowCountMarker(string text)
    {
        var lines = text.Split('\n').Where(l => l.Length > 0 && !l.StartsWith("SET NOCOUNT", StringComparison.Ordinal)
                                                 && !l.StartsWith("SELECT @@ROWCOUNT", StringComparison.Ordinal));
        return string.Join('\n', lines);
    }

    /// <summary>See <see cref="CsvValue.TryConvert"/>. Culture-independent; ambiguous input is rejected, never guessed.</summary>
    public static bool TryConvert(string cell, CsvSqlType type, bool emptyTextAsNull, out object? value, out string? error)
        => CsvValue.TryConvert(cell, type, emptyTextAsNull, out value, out error);
}
