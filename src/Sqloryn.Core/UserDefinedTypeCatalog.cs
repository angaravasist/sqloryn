using System.Globalization;
using System.Text;

namespace Sqloryn.Core;

/// <summary>One user-defined type. <see cref="SystemType"/> is the base type display, or "table", or "not returned".</summary>
public sealed record UserDefinedType(string Schema, string Name, string SystemType, bool IsTableType);

/// <summary>Projects <see cref="MetadataCatalog.ListUserDefinedTypes"/> into explorer titles. Does not run SQL.</summary>
public static class UserDefinedTypeCatalog
{
    public static IReadOnlyList<UserDefinedType> Parse(RawRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = new List<UserDefinedType>(rows.Rows.Count);
        foreach (var row in rows.Rows)
        {
            if (row.Length < 7)
                continue;
            var table = row[6] is true;
            var system = SystemTypeDisplay(TextOrNull(row[2]), ToInt(row[3]), ToInt(row[4]), ToInt(row[5]), table);
            list.Add(new UserDefinedType(Text(row[0]), Text(row[1]), system, table));
        }
        return list;
    }

    public static string SystemTypeDisplay(string? baseName, int maxLength, int precision, int scale, bool isTableType)
    {
        if (isTableType)
            return "table";
        if (string.IsNullOrEmpty(baseName))
            return "not returned";
        var name = baseName.ToLowerInvariant();
        return name switch
        {
            "nchar" or "nvarchar" or "char" or "varchar" or "binary" or "varbinary"
                => maxLength < 0
                    ? name + "(max)"
                    : name is "nchar" or "nvarchar" && maxLength > 0
                        ? name + "(" + (maxLength / 2).ToString(CultureInfo.InvariantCulture) + ")"
                        : name + "(" + maxLength.ToString(CultureInfo.InvariantCulture) + ")",
            "decimal" or "numeric" => name + "(" + precision.ToString(CultureInfo.InvariantCulture) + "," + scale.ToString(CultureInfo.InvariantCulture) + ")",
            "datetime2" or "datetimeoffset" or "time" => name + "(" + scale.ToString(CultureInfo.InvariantCulture) + ")",
            _ => name
        };
    }

    public static string Title(UserDefinedType item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var schema = SchemaObjectCatalog.OneLine(item.Schema);
        var name = SchemaObjectCatalog.OneLine(item.Name);
        return string.IsNullOrEmpty(schema) ? name : schema + "." + name;
    }

    public static string ExpandTitle(UserDefinedType item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return "System type · " + SchemaObjectCatalog.OneLine(item.SystemType);
    }

    public static string Describe(UserDefinedType item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var sb = new StringBuilder();
        void Line(string label, string value) => sb.Append(label.PadRight(10)).Append(SchemaObjectCatalog.OneLine(value)).Append('\n');
        Line("Name", item.Name);
        Line("Schema", item.Schema);
        Line("System", item.SystemType);
        Line("Table type", item.IsTableType ? "yes" : "no");
        return sb.ToString().TrimEnd();
    }

    static string Text(object? value) => TextOrNull(value) ?? "";

    static string? TextOrNull(object? value)
        => value is null or DBNull or RawCellOmitted ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    static int ToInt(object? value)
        => value is null or DBNull or RawCellOmitted ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
}
