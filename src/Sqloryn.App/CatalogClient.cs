using Sqloryn.Core;

namespace Sqloryn;

public sealed record CatalogColumn(
    string Name,
    string TypeName,
    bool IsNullable,
    bool IsIdentity,
    int MaxLength,
    int Precision,
    int Scale)
{
    /// <summary>Compact type display such as nvarchar(50), decimal(18,2), int.</summary>
    public string TypeDisplay => TypeName.ToLowerInvariant() switch
    {
        "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary"
            => MaxLength < 0 ? $"{TypeName}(max)" : $"{TypeName}({MaxLength})",
        "decimal" or "numeric" => $"{TypeName}({Precision},{Scale})",
        "datetime2" or "datetimeoffset" or "time" => $"{TypeName}({Scale})",
        _ => TypeName
    };
}

/// <summary>
/// Runs <see cref="MetadataCatalog"/> queries through a session. Schema and object names are bound as parameters.
/// </summary>
static class CatalogClient
{
    const int MaxItems = 5_000;

    public static async Task<IReadOnlyList<string>> ListSchemasAsync(SqlSession session, string database, CancellationToken ct = default)
    {
        var rows = await session.QueryRawAsync(MetadataCatalog.ListSchemas(database), null, MaxItems, ct);
        return rows.Rows.Select(r => Convert.ToString(r[0]) ?? "").ToList();
    }

    public static Task<IReadOnlyList<string>> ListTablesAsync(SqlSession session, string database, string schema, CancellationToken ct = default)
        => ListObjectsAsync(session, MetadataCatalog.ListTables(database, schema), schema, ct);

    public static Task<IReadOnlyList<string>> ListViewsAsync(SqlSession session, string database, string schema, CancellationToken ct = default)
        => ListObjectsAsync(session, MetadataCatalog.ListViews(database, schema), schema, ct);

    public static Task<IReadOnlyList<string>> ListProceduresAsync(SqlSession session, string database, string schema, CancellationToken ct = default)
        => ListObjectsAsync(session, MetadataCatalog.ListProcedures(database, schema), schema, ct);

    static async Task<IReadOnlyList<string>> ListObjectsAsync(SqlSession session, string sql, string schema, CancellationToken ct)
    {
        var rows = await session.QueryRawAsync(
            sql, [new EditParameter(MetadataCatalog.SchemaParameter, schema)], MaxItems, ct);
        return rows.Rows.Select(r => Convert.ToString(r[1]) ?? "").ToList();
    }

    public static async Task<IReadOnlyList<CatalogColumn>> ListColumnsAsync(
        SqlSession session, string database, string schema, string table, CancellationToken ct = default)
    {
        var rows = await session.QueryRawAsync(
            MetadataCatalog.ListColumns(database, schema, table),
            [new EditParameter(MetadataCatalog.SchemaParameter, schema), new EditParameter(MetadataCatalog.TableParameter, table)],
            4_096,
            ct);
        return rows.Rows.Select(r => new CatalogColumn(
            Convert.ToString(r[0]) ?? "",
            Convert.ToString(r[1]) ?? "",
            r[2] is true,
            r[3] is true,
            Convert.ToInt32(r[4] ?? 0, System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(r[5] ?? 0, System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(r[6] ?? 0, System.Globalization.CultureInfo.InvariantCulture))).ToList();
    }

    public static async Task<IReadOnlyList<string>> ListPrimaryKeyAsync(
        SqlSession session, string database, string schema, string table, CancellationToken ct = default)
    {
        var rows = await session.QueryRawAsync(
            MetadataCatalog.ListPrimaryKeyColumns(database, schema, table),
            [new EditParameter(MetadataCatalog.SchemaParameter, schema), new EditParameter(MetadataCatalog.TableParameter, table)],
            256,
            ct);
        return rows.Rows.Select(r => Convert.ToString(r[0]) ?? "").ToList();
    }
}
