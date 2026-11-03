using Sqloryn.Core;

namespace Sqloryn;

public sealed record CatalogColumn(
    string Name,
    string TypeName,
    bool IsNullable,
    bool IsIdentity,
    int MaxLength,
    int Precision,
    int Scale,
    bool IsComputed = false)
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

/// <summary>A catalog read plus whether the row cap or byte budget stopped it early.</summary>
public sealed record CatalogListing<T>(IReadOnlyList<T> Items, bool Truncated, bool ByteBudgetExhausted)
{
    /// <summary>Null when complete.</summary>
    public string? TruncationNote => CatalogTruncation.Describe(Items.Count, Truncated, ByteBudgetExhausted);
}

/// <summary>
/// Runs <see cref="MetadataCatalog"/> queries through a session. Schema and object names are bound as parameters.
/// </summary>
static class CatalogClient
{
    const int MaxItems = 5_000;

    public static async Task<CatalogListing<string>> ListSchemasAsync(SqlSession session, string database, CancellationToken ct = default)
    {
        var rows = await session.QueryRawAsync(MetadataCatalog.ListSchemas(database), null, MaxItems, ct);
        return Listing(rows, rows.Rows.Select(r => Convert.ToString(r[0]) ?? "").ToList());
    }

    static CatalogListing<T> Listing<T>(RawRows rows, IReadOnlyList<T> items)
        => new(items, rows.Truncated, rows.ByteBudgetExhausted);

    public static Task<CatalogListing<string>> ListTablesAsync(SqlSession session, string database, string schema, CancellationToken ct = default)
        => ListObjectsAsync(session, MetadataCatalog.ListTables(database, schema), schema, ct);

    public static Task<CatalogListing<string>> ListViewsAsync(SqlSession session, string database, string schema, CancellationToken ct = default)
        => ListObjectsAsync(session, MetadataCatalog.ListViews(database, schema), schema, ct);

    public static Task<CatalogListing<string>> ListProceduresAsync(SqlSession session, string database, string schema, CancellationToken ct = default)
        => ListObjectsAsync(session, MetadataCatalog.ListProcedures(database, schema), schema, ct);

    public static Task<CatalogListing<SchemaObject>> ListKeysAsync(SqlSession session, string database, string schema, CancellationToken ct = default)
        => ListSchemaObjectsAsync(session, MetadataCatalog.ListKeys(database, schema), schema, ct);

    public static Task<CatalogListing<SchemaObject>> ListIndexesAsync(SqlSession session, string database, string schema, CancellationToken ct = default)
        => ListSchemaObjectsAsync(session, MetadataCatalog.ListIndexes(database, schema), schema, ct);

    public static Task<CatalogListing<SchemaObject>> ListFunctionsAsync(SqlSession session, string database, string schema, CancellationToken ct = default)
        => ListSchemaObjectsAsync(session, MetadataCatalog.ListFunctions(database, schema), schema, ct);

    static async Task<CatalogListing<SchemaObject>> ListSchemaObjectsAsync(SqlSession session, string sql, string schema, CancellationToken ct)
    {
        var rows = await session.QueryRawAsync(
            sql, [new EditParameter(MetadataCatalog.SchemaParameter, schema)], MaxItems, ct);
        return Listing(rows, SchemaObjectCatalog.Parse(rows));
    }

    static async Task<CatalogListing<string>> ListObjectsAsync(SqlSession session, string sql, string schema, CancellationToken ct)
    {
        var rows = await session.QueryRawAsync(
            sql, [new EditParameter(MetadataCatalog.SchemaParameter, schema)], MaxItems, ct);
        return Listing(rows, rows.Rows.Select(r => Convert.ToString(r[1]) ?? "").ToList());
    }

    /// <summary>Strict: throws if the column list was truncated, so design/edit never act on a partial set.</summary>
    public static async Task<IReadOnlyList<CatalogColumn>> ListColumnsAsync(
        SqlSession session, string database, string schema, string table, CancellationToken ct = default)
    {
        var listing = await ListColumnsPageAsync(session, database, schema, table, ct);
        if (listing.Truncated)
            throw new InvalidOperationException("Column list was truncated; refusing to continue with a partial set.");
        return listing.Items;
    }

    public static async Task<CatalogListing<CatalogColumn>> ListColumnsPageAsync(
        SqlSession session, string database, string schema, string table, CancellationToken ct = default)
    {
        var rows = await session.QueryRawAsync(
            MetadataCatalog.ListColumns(database, schema, table),
            [new EditParameter(MetadataCatalog.SchemaParameter, schema), new EditParameter(MetadataCatalog.TableParameter, table)],
            4_096,
            ct);
        return Listing(rows, rows.Rows.Select(r => new CatalogColumn(
            Convert.ToString(r[0]) ?? "",
            Convert.ToString(r[1]) ?? "",
            r[2] is true,
            r[3] is true,
            Convert.ToInt32(r[4] ?? 0, System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(r[5] ?? 0, System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(r[6] ?? 0, System.Globalization.CultureInfo.InvariantCulture),
            r.Length > 7 && r[7] is true)).ToList());
    }

    public static async Task<IReadOnlyList<string>> ListPrimaryKeyAsync(
        SqlSession session, string database, string schema, string table, CancellationToken ct = default)
    {
        var rows = await session.QueryRawAsync(
            MetadataCatalog.ListPrimaryKeyColumns(database, schema, table),
            [new EditParameter(MetadataCatalog.SchemaParameter, schema), new EditParameter(MetadataCatalog.TableParameter, table)],
            256,
            ct);
        if (rows.Truncated)
            throw new InvalidOperationException("Primary key column list was truncated; refusing to continue with a partial key.");
        return rows.Rows.Select(r => Convert.ToString(r[0]) ?? "").ToList();
    }

    public static async Task<bool> HasInsteadOfTriggerAsync(
        SqlSession session, string database, string schema, string table, CancellationToken ct = default)
    {
        var rows = await session.QueryRawAsync(
            MetadataCatalog.CountEnabledInsteadOfTriggers(database, schema, table),
            [new EditParameter(MetadataCatalog.SchemaParameter, schema), new EditParameter(MetadataCatalog.TableParameter, table)],
            1,
            ct);
        return rows.Rows.Count > 0 && Convert.ToInt32(rows.Rows[0][0] ?? 0, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }
}
