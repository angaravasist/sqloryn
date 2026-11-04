using System.Globalization;
using System.Text;

namespace Sqloryn.Core;

/// <summary>Counts shown in the object overview. <see cref="KeyCount"/> is primary + unique + foreign keys.</summary>
public sealed record ObjectCounts(
    int PrimaryKeys, int UniqueKeys, int ForeignKeys, int Checks, int Defaults, int Triggers, int Indexes)
{
    public int KeyCount => PrimaryKeys + UniqueKeys + ForeignKeys;

    /// <summary>Parses the single row from <see cref="MetadataCatalog.TableOverviewCounts"/>; null when no row was visible.</summary>
    public static ObjectCounts? Parse(RawRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Rows.Count == 0 || rows.Rows[0].Length < 7)
            return null;
        var r = rows.Rows[0];
        int N(int i) => r[i] is null or DBNull or RawCellOmitted ? 0 : Convert.ToInt32(r[i], CultureInfo.InvariantCulture);
        return new ObjectCounts(N(0), N(1), N(2), N(3), N(4), N(5), N(6));
    }
}

/// <summary>Column facts for the overview, independent of the App's catalog record.</summary>
public sealed record OverviewColumn(string Name, string TypeDisplay, bool IsNullable, bool IsIdentity, bool IsComputed);

/// <summary>Builds the compact, single-line-per-fact inspector text for a selected table or view.</summary>
public static class ObjectOverviewFormatter
{
    public const int DefaultColumnLines = 12;

    public static string Format(
        string kind,
        string qualifiedName,
        IReadOnlyList<OverviewColumn> columns,
        bool columnsTruncated,
        IReadOnlyList<string> primaryKeyColumns,
        ObjectCounts? counts,
        int maxColumnLines = DefaultColumnLines)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(primaryKeyColumns);
        var sb = new StringBuilder();
        void Line(string label, string value) => sb.Append(label.PadRight(9)).Append(value).Append('\n');
        static string One(string s) => SchemaObjectCatalog.OneLine(s);

        sb.Append("OVERVIEW · ").Append(One(kind)).Append('\n');
        Line("Object", One(qualifiedName));

        var count = columns.Count.ToString(CultureInfo.InvariantCulture) + (columnsTruncated ? "+" : "");
        var identity = columns.Count(c => c.IsIdentity);
        var nullable = columns.Count(c => c.IsNullable);
        var computed = columns.Count(c => c.IsComputed);
        Line("Columns", $"{count} ({identity} identity · {nullable} nullable · {computed} computed)");
        if (columnsTruncated)
            Line("", "Column list truncated; counts above are for the columns read.");

        if (counts is null)
        {
            Line("Keys", "not available (counts could not be read)");
        }
        else
        {
            var pk = primaryKeyColumns.Count > 0
                ? "PK (" + string.Join(", ", primaryKeyColumns.Select(One)) + ")"
                : counts.PrimaryKeys > 0 ? "PK" : "no primary key";
            Line("Keys", $"{pk} · {counts.UniqueKeys} unique · {counts.ForeignKeys} foreign");
            Line("Other", $"{counts.Checks} check · {counts.Defaults} default · {counts.Triggers} trigger · {counts.Indexes} index");
        }

        sb.Append('\n');
        var shown = Math.Min(columns.Count, Math.Max(0, maxColumnLines));
        foreach (var c in columns.Take(shown))
            sb.Append(One(c.Name)).Append(" : ").Append(One(c.TypeDisplay))
              .Append(c.IsIdentity ? " · identity" : "")
              .Append(c.IsComputed ? " · computed" : "")
              .Append(c.IsNullable ? "" : " · NOT NULL").Append('\n');
        if (columns.Count > shown)
            sb.Append("… ").Append((columns.Count - shown).ToString(CultureInfo.InvariantCulture)).Append(" more columns (expand the object for the full list)\n");
        return sb.ToString().TrimEnd();
    }
}
