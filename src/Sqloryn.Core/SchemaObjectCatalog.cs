using System.Text;

namespace Sqloryn.Core;

/// <summary>
/// One key, index, function, constraint, trigger, sequence or synonym row. <see cref="Parent"/> is the owning
/// table/view (null for schema-scoped objects). <see cref="Detail"/> is server-supplied descriptive text (constraint
/// definition, referenced table, trigger timing, sequence type, synonym base) and is untrusted.
/// </summary>
public sealed record SchemaObject(
    string Name,
    string? Parent,
    string TypeDescription,
    bool IsUnique,
    string? Detail = null,
    bool IsDisabled = false);

/// <summary>Parses and titles rows from <see cref="MetadataCatalog.ListKeys"/>, ListIndexes, ListFunctions and the object-detail listings.</summary>
public static class SchemaObjectCatalog
{
    public static IReadOnlyList<SchemaObject> Parse(RawRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = new List<SchemaObject>(rows.Rows.Count);
        foreach (var r in rows.Rows)
        {
            if (r.Length < 4)
                continue;
            list.Add(new SchemaObject(
                Convert.ToString(r[1], System.Globalization.CultureInfo.InvariantCulture) ?? "",
                r[2] is null or DBNull ? null : Convert.ToString(r[2], System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToString(r[3], System.Globalization.CultureInfo.InvariantCulture) ?? "",
                r.Length > 4 && r[4] is true,
                r.Length > 5 ? DetailText(r[5]) : null,
                r.Length > 6 && r[6] is true));
        }
        return list;
    }

    /// <summary>Null stays null; an oversized cell the reader refused to load is named, never shown as empty.</summary>
    static string? DetailText(object? value) => value switch
    {
        null or DBNull => null,
        RawCellOmitted omitted => $"({omitted.Reason})",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
    };

    const int MaxTitleDetail = 120;

    /// <summary>Single-line tree title. Server-supplied text has control characters replaced.</summary>
    public static string Title(string kind, SchemaObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var name = OneLine(item.Name);
        var parent = item.Parent is null ? null : OneLine(item.Parent);
        return kind switch
        {
            "key" => $"{parent}.{name} · {KeyKind(item.TypeDescription)}",
            "index" => $"{parent}.{name} · {Words(item.TypeDescription)}{(item.IsUnique ? " · unique" : "")}",
            "function" => $"{name} · {FunctionKind(item.TypeDescription)}",
            "foreignkey" => $"{name}{Arrow(item.Detail)}{Disabled(item)}",
            "constraint" => $"{name} · {(item.TypeDescription.StartsWith("DEFAULT", StringComparison.Ordinal) ? "default" : "check")}{Disabled(item)}{Dot(item.Detail)}",
            "trigger" => $"{name}{Dot(item.Detail is null ? null : Words(item.Detail))}{Disabled(item)}",
            "sequence" => $"{name}{Dot(item.Detail)}",
            "synonym" => $"{name}{Arrow(item.Detail)}",
            _ => name
        };
    }

    /// <summary>Multi-line properties text for a selected explorer node. Server text is not shortened here.</summary>
    public static string Describe(string kind, SchemaObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var sb = new StringBuilder();
        void Line(string label, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                sb.Append(label.PadRight(11)).Append(OneLine(value)).Append('\n');
        }
        Line("Name", item.Name);
        Line(kind is "foreignkey" or "constraint" or "trigger" or "key" or "index" ? "Table" : "Parent", item.Parent);
        Line("Type", item.TypeDescription);
        if (item.IsUnique)
            Line("Unique", "yes");
        Line(kind switch
        {
            "foreignkey" => "References",
            "constraint" => "Definition",
            "trigger" => "Timing",
            "synonym" => "Base object",
            _ => "Detail"
        }, item.Detail);
        if (item.IsDisabled)
            Line("Disabled", "yes");
        return sb.ToString().TrimEnd();
    }

    static string Disabled(SchemaObject item) => item.IsDisabled ? " · disabled" : "";

    static string Dot(string? detail) => detail is null ? "" : " · " + Shorten(detail);

    static string Arrow(string? detail) => detail is null ? "" : " → " + Shorten(detail);

    static string Shorten(string detail)
    {
        var line = OneLine(detail);
        return line.Length <= MaxTitleDetail ? line : line[..MaxTitleDetail] + "…";
    }

    static string KeyKind(string typeDescription) => typeDescription switch
    {
        "PRIMARY_KEY_CONSTRAINT" => "primary key",
        "UNIQUE_CONSTRAINT" => "unique",
        "FOREIGN_KEY_CONSTRAINT" => "foreign key",
        _ => Words(typeDescription)
    };

    static string FunctionKind(string typeDescription) => typeDescription switch
    {
        "SQL_SCALAR_FUNCTION" => "scalar",
        "SQL_INLINE_TABLE_VALUED_FUNCTION" => "inline table-valued",
        "SQL_TABLE_VALUED_FUNCTION" => "table-valued",
        _ => Words(typeDescription)
    };

    static string Words(string value) => OneLine(value).Replace('_', ' ').ToLowerInvariant();

    /// <summary>Replaces control and line-separator characters so server text stays on one line.</summary>
    public static string OneLine(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(char.IsControl(ch) || ch is '\u2028' or '\u2029' ? ' ' : ch);
        return sb.ToString();
    }
}
