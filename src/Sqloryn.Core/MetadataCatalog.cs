namespace Sqloryn.Core;

/// <summary>
/// Read-only catalog queries. Database names are quoted identifiers (they cannot be parameters);
/// schema and object names are bound through the <see cref="SchemaParameter"/> and <see cref="TableParameter"/> parameters.
/// Every query is fully qualified with a database prefix, so no USE / database context change is needed.
/// </summary>
public static class MetadataCatalog
{
    public const string SchemaParameter = "@schema";
    public const string TableParameter = "@table";

    public static string ListDatabases()
        => """
           SELECT name FROM sys.databases
           WHERE HAS_DBACCESS(name) = 1
           ORDER BY name;
           """;

    public static string ListSchemas(string database)
    {
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT name FROM {db}.sys.schemas
            WHERE schema_id < 16384
            ORDER BY name;
            """;
    }

    /// <summary>Columns: schema name, table name. Requires <see cref="SchemaParameter"/>.</summary>
    public static string ListTables(string database, string schema)
    {
        RequireName(schema);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT s.name, o.name
            FROM {db}.sys.tables AS o
            INNER JOIN {db}.sys.schemas AS s ON o.schema_id = s.schema_id
            WHERE s.name = {SchemaParameter} AND o.is_ms_shipped = 0
            ORDER BY o.name;
            """;
    }

    /// <summary>Columns: schema name, view name. Requires <see cref="SchemaParameter"/>.</summary>
    public static string ListViews(string database, string schema)
    {
        RequireName(schema);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT s.name, o.name
            FROM {db}.sys.views AS o
            INNER JOIN {db}.sys.schemas AS s ON o.schema_id = s.schema_id
            WHERE s.name = {SchemaParameter} AND o.is_ms_shipped = 0
            ORDER BY o.name;
            """;
    }

    /// <summary>Columns: schema name, procedure name. Requires <see cref="SchemaParameter"/>.</summary>
    public static string ListProcedures(string database, string schema)
    {
        RequireName(schema);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT s.name, o.name
            FROM {db}.sys.procedures AS o
            INNER JOIN {db}.sys.schemas AS s ON o.schema_id = s.schema_id
            WHERE s.name = {SchemaParameter} AND o.is_ms_shipped = 0
            ORDER BY o.name;
            """;
    }

    /// <summary>
    /// Columns: schema name, key name, parent table name, type_desc, is_unique (1 for primary/unique keys).
    /// Primary, unique and foreign keys whose parent table is in the schema. Requires <see cref="SchemaParameter"/>.
    /// </summary>
    public static string ListKeys(string database, string schema)
    {
        RequireName(schema);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT s.name, k.name, p.name, k.type_desc, CONVERT(bit, 1)
            FROM {db}.sys.key_constraints AS k
            INNER JOIN {db}.sys.objects AS p ON k.parent_object_id = p.object_id
            INNER JOIN {db}.sys.schemas AS s ON p.schema_id = s.schema_id
            WHERE s.name = {SchemaParameter} AND p.is_ms_shipped = 0
            UNION ALL
            SELECT s.name, f.name, p.name, f.type_desc, CONVERT(bit, 0)
            FROM {db}.sys.foreign_keys AS f
            INNER JOIN {db}.sys.objects AS p ON f.parent_object_id = p.object_id
            INNER JOIN {db}.sys.schemas AS s ON p.schema_id = s.schema_id
            WHERE s.name = {SchemaParameter} AND p.is_ms_shipped = 0
            ORDER BY 3, 2;
            """;
    }

    /// <summary>
    /// Columns: schema name, index name, parent table or view name, type_desc, is_unique.
    /// Named, non-hypothetical indexes (heaps excluded). Requires <see cref="SchemaParameter"/>.
    /// </summary>
    public static string ListIndexes(string database, string schema)
    {
        RequireName(schema);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT s.name, i.name, o.name, i.type_desc, CONVERT(bit, i.is_unique)
            FROM {db}.sys.indexes AS i
            INNER JOIN {db}.sys.objects AS o ON i.object_id = o.object_id
            INNER JOIN {db}.sys.schemas AS s ON o.schema_id = s.schema_id
            WHERE s.name = {SchemaParameter} AND o.is_ms_shipped = 0 AND o.type IN ('U', 'V')
              AND i.index_id > 0 AND i.name IS NOT NULL AND i.is_hypothetical = 0
            ORDER BY o.name, i.name;
            """;
    }

    /// <summary>
    /// Columns: schema name, function name, parent (always NULL), type_desc, is_unique (always 0).
    /// Scalar, inline and multi-statement table-valued and CLR functions. Requires <see cref="SchemaParameter"/>.
    /// </summary>
    public static string ListFunctions(string database, string schema)
    {
        RequireName(schema);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT s.name, o.name, CONVERT(nvarchar(128), NULL), o.type_desc, CONVERT(bit, 0)
            FROM {db}.sys.objects AS o
            INNER JOIN {db}.sys.schemas AS s ON o.schema_id = s.schema_id
            WHERE s.name = {SchemaParameter} AND o.is_ms_shipped = 0 AND o.type IN ('FN', 'IF', 'TF', 'FS', 'FT')
            ORDER BY o.name;
            """;
    }

    /// <summary>
    /// Columns: name, type name, is_nullable, is_identity, max length in characters (-1 = MAX), precision, scale, is_computed.
    /// Requires <see cref="SchemaParameter"/> and <see cref="TableParameter"/>.
    /// </summary>
    public static string ListColumns(string database, string schema, string table)
    {
        RequireName(schema);
        RequireName(table);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT c.name, t.name, c.is_nullable, c.is_identity,
                   CASE WHEN t.name IN (N'nchar', N'nvarchar') AND c.max_length > 0 THEN c.max_length / 2 ELSE c.max_length END,
                   c.precision, c.scale, c.is_computed
            FROM {db}.sys.columns AS c
            INNER JOIN {db}.sys.types AS t ON c.user_type_id = t.user_type_id
            INNER JOIN {db}.sys.objects AS o ON c.object_id = o.object_id
            INNER JOIN {db}.sys.schemas AS s ON o.schema_id = s.schema_id
            WHERE s.name = {SchemaParameter} AND o.name = {TableParameter} AND o.type IN ('U', 'V')
            ORDER BY c.column_id;
            """;
    }

    /// <summary>
    /// One int column: number of enabled INSTEAD OF triggers on the table or view. Requires <see cref="SchemaParameter"/>
    /// and <see cref="TableParameter"/>. Edit-in-grid refuses such objects because affected-row counts are unreliable.
    /// </summary>
    public static string CountEnabledInsteadOfTriggers(string database, string schema, string table)
    {
        RequireName(schema);
        RequireName(table);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT CONVERT(int, COUNT(*))
            FROM {db}.sys.triggers AS tr
            INNER JOIN {db}.sys.objects AS o ON o.object_id = tr.parent_id
            INNER JOIN {db}.sys.schemas AS s ON o.schema_id = s.schema_id
            WHERE tr.is_instead_of_trigger = 1 AND tr.is_disabled = 0
              AND s.name = {SchemaParameter} AND o.name = {TableParameter};
            """;
    }

    /// <summary>Columns: column name, in key order. Requires <see cref="SchemaParameter"/> and <see cref="TableParameter"/>.</summary>
    public static string ListPrimaryKeyColumns(string database, string schema, string table)
    {
        RequireName(schema);
        RequireName(table);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT c.name
            FROM {db}.sys.indexes AS i
            INNER JOIN {db}.sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN {db}.sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            INNER JOIN {db}.sys.objects AS o ON o.object_id = i.object_id
            INNER JOIN {db}.sys.schemas AS s ON o.schema_id = s.schema_id
            WHERE i.is_primary_key = 1 AND s.name = {SchemaParameter} AND o.name = {TableParameter}
            ORDER BY ic.key_ordinal;
            """;
    }

    static void RequireName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 || name.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("Invalid identifier.");
    }
}
