using System.Globalization;
using System.Text;

namespace Sqloryn.Core;

/// <summary>One <c>sys.objects</c> row used by the read-only properties sheet.</summary>
public sealed record CatalogObjectFacts(
    string Schema,
    string Name,
    string TypeName,
    DateTime? Created,
    DateTime? Modified);

/// <summary>One <c>sys.databases</c> row. Modified is not a column of that view.</summary>
public sealed record CatalogDatabaseFacts(string Name, DateTime? Created);

/// <summary>Parses catalog property rows. A missing row stays null so dates are never invented.</summary>
public static class ObjectPropertyCatalog
{
    public static CatalogObjectFacts? ParseObject(RawRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows.Rows)
        {
            if (row.Length < 5)
                continue;
            return new CatalogObjectFacts(
                Text(row[0]),
                Text(row[1]),
                Text(row[2]),
                ReadDate(row[3]),
                ReadDate(row[4]));
        }
        return null;
    }

    public static CatalogDatabaseFacts? ParseDatabase(RawRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows.Rows)
        {
            if (row.Length < 2)
                continue;
            return new CatalogDatabaseFacts(Text(row[0]), ReadDate(row[1]));
        }
        return null;
    }

    static string Text(object? value)
        => value is null or DBNull or RawCellOmitted
            ? ""
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    static DateTime? ReadDate(object? value) => value switch
    {
        null or DBNull or RawCellOmitted => null,
        DateTime dt => dt,
        DateTimeOffset dto => dto.DateTime,
        _ => null
    };
}

/// <summary>
/// Read-only properties text. Catalog dates are included only when a row actually returned them.
/// The sheet never builds or runs a change script.
/// </summary>
public static class ObjectPropertySheet
{
    public static bool ReadsCatalog(string kind) => kind is
        "database" or "table" or "view" or "procedure" or "function" or
        "sequence" or "synonym" or "trigger" or "constraint" or "foreignkey" or "key";

    public static string FormatCatalogObject(CatalogObjectFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var sb = new StringBuilder();
        Line(sb, "Name", facts.Name);
        Line(sb, "Schema", facts.Schema);
        Line(sb, "Type", facts.TypeName);
        Line(sb, "Created", facts.Created is DateTime created ? FormatTimestamp(created) : "not returned by the catalog");
        Line(sb, "Modified", facts.Modified is DateTime modified ? FormatTimestamp(modified) : "not returned by the catalog");
        return Finish(sb, "Read from the catalog. Dates are catalog datetimes and are not converted. Nothing was changed.");
    }

    public static string FormatDatabase(CatalogDatabaseFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var sb = new StringBuilder();
        Line(sb, "Name", facts.Name);
        Line(sb, "Type", "Database");
        Line(sb, "Created", facts.Created is DateTime created ? FormatTimestamp(created) : "not returned by the catalog");
        Line(sb, "Modified", "not provided by sys.databases");
        return Finish(sb, "Read from the catalog. The created value is a catalog datetime and is not converted. Nothing was changed.");
    }

    public static string FormatNoRow(string name)
    {
        var sb = new StringBuilder();
        Line(sb, "Name", name);
        sb.Append("The catalog returned no row for this object. Dates were not invented.").Append('\n');
        return Finish(sb, "Nothing was changed.");
    }

    /// <summary>Properties known from the selected node when this kind has no create/modify columns.</summary>
    public static string FormatLocal(string kind, string name, string? schema, string? detail)
    {
        var sb = new StringBuilder();
        Line(sb, "Name", name);
        Line(sb, "Schema", schema);
        Line(sb, "Type", KindLabel(kind));
        if (!string.IsNullOrEmpty(detail))
            Line(sb, "System", detail);
        Line(sb, "Created", "not provided by the catalog for this item");
        Line(sb, "Modified", "not provided by the catalog for this item");
        return Finish(sb, "Read from the object already loaded. The catalog was not asked for dates it does not provide. Nothing was changed.");
    }

    public static string FormatTimestamp(DateTime value)
        => value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public static string KindLabel(string kind) => kind switch
    {
        "database" => "Database",
        "schema" => "Schema",
        "table" => "Table",
        "view" => "View",
        "procedure" => "Stored procedure",
        "function" => "Function",
        "sequence" => "Sequence",
        "synonym" => "Synonym",
        "trigger" => "Trigger",
        "constraint" => "Constraint",
        "foreignkey" => "Foreign key",
        "key" => "Key",
        "index" => "Index",
        "column" => "Column",
        "usertype" => "User-defined type",
        "typesystem" => "System type",
        "types" => "User-defined types",
        _ => "Item"
    };

    static void Line(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        sb.Append(label.PadRight(10)).Append(SchemaObjectCatalog.OneLine(value)).Append('\n');
    }

    static string Finish(StringBuilder sb, string note)
    {
        sb.Append('\n').Append(note);
        return sb.ToString().TrimEnd();
    }
}
