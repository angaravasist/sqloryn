using System.Text;

namespace Sqloryn.Core;

/// <summary>One key, index or function row. <see cref="Parent"/> is the owning table/view (null for functions).</summary>
public sealed record SchemaObject(string Name, string? Parent, string TypeDescription, bool IsUnique);

/// <summary>Parses and titles rows from <see cref="MetadataCatalog.ListKeys"/>, ListIndexes and ListFunctions.</summary>
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
                r.Length > 4 && r[4] is true));
        }
        return list;
    }

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
            _ => name
        };
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

    static string OneLine(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(char.IsControl(ch) || ch is '\u2028' or '\u2029' ? ' ' : ch);
        return sb.ToString();
    }
}
