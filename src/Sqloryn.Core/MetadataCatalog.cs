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

    // Object-detail listings share one row shape, parsed by SchemaObjectCatalog.Parse:
    //   schema name, object name, parent (table/view or NULL), type_desc, is_unique, detail text, is_disabled.

    /// <summary>
    /// Foreign keys on one table; detail is the referenced "schema.table". Requires <see cref="SchemaParameter"/>
    /// and <see cref="TableParameter"/>.
    /// </summary>
    public static string ListForeignKeys(string database, string schema, string table)
    {
        RequireName(schema);
        RequireName(table);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT s.name, f.name, p.name, f.type_desc, CONVERT(bit, 0),
                   CONVERT(nvarchar(300), rs.name + N'.' + rp.name), f.is_disabled
            FROM {db}.sys.foreign_keys AS f
            INNER JOIN {db}.sys.objects AS p ON f.parent_object_id = p.object_id
            INNER JOIN {db}.sys.schemas AS s ON p.schema_id = s.schema_id
            INNER JOIN {db}.sys.objects AS rp ON f.referenced_object_id = rp.object_id
            INNER JOIN {db}.sys.schemas AS rs ON rp.schema_id = rs.schema_id
            WHERE s.name = {SchemaParameter} AND p.name = {TableParameter}
            ORDER BY f.name;
            """;
    }

    /// <summary>
    /// CHECK and DEFAULT constraints on one table; detail is the definition text. Requires
    /// <see cref="SchemaParameter"/> and <see cref="TableParameter"/>.
    /// </summary>
    public static string ListConstraints(string database, string schema, string table)
    {
        RequireName(schema);
        RequireName(table);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT s.name, c.name, p.name, c.type_desc, CONVERT(bit, 0), c.definition, c.is_disabled
            FROM {db}.sys.check_constraints AS c
            INNER JOIN {db}.sys.objects AS p ON c.parent_object_id = p.object_id
            INNER JOIN {db}.sys.schemas AS s ON p.schema_id = s.schema_id
            WHERE s.name = {SchemaParameter} AND p.name = {TableParameter}
            UNION ALL
            SELECT s.name, d.name, p.name, d.type_desc, CONVERT(bit, 0), d.definition, CONVERT(bit, 0)
            FROM {db}.sys.default_constraints AS d
            INNER JOIN {db}.sys.objects AS p ON d.parent_object_id = p.object_id
            INNER JOIN {db}.sys.schemas AS s ON p.schema_id = s.schema_id
            WHERE s.name = {SchemaParameter} AND p.name = {TableParameter}
            ORDER BY 4, 2;
            """;
    }

    /// <summary>
    /// DML triggers on one table or view; detail is AFTER or INSTEAD OF. Database-scoped DDL triggers are excluded.
    /// Requires <see cref="SchemaParameter"/> and <see cref="TableParameter"/>.
    /// </summary>
    public static string ListTriggers(string database, string schema, string table)
    {
        RequireName(schema);
        RequireName(table);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT s.name, t.name, p.name, t.type_desc, CONVERT(bit, 0),
                   CASE WHEN t.is_instead_of_trigger = 1 THEN N'INSTEAD OF' ELSE N'AFTER' END, t.is_disabled
            FROM {db}.sys.triggers AS t
            INNER JOIN {db}.sys.objects AS p ON t.parent_id = p.object_id
            INNER JOIN {db}.sys.schemas AS s ON p.schema_id = s.schema_id
            WHERE t.parent_class = 1 AND s.name = {SchemaParameter} AND p.name = {TableParameter}
            ORDER BY t.name;
            """;
    }

    /// <summary>Sequences in a schema; detail is "type · start N · increment N". Requires <see cref="SchemaParameter"/>.</summary>
    public static string ListSequences(string database, string schema)
    {
        RequireName(schema);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT s.name, q.name, CONVERT(nvarchar(128), NULL), q.type_desc, CONVERT(bit, 0),
                   CONVERT(nvarchar(300), t.name + N' · start ' + CONVERT(nvarchar(40), q.start_value) +
                       N' · increment ' + CONVERT(nvarchar(40), q.increment)),
                   CONVERT(bit, 0)
            FROM {db}.sys.sequences AS q
            INNER JOIN {db}.sys.schemas AS s ON q.schema_id = s.schema_id
            INNER JOIN {db}.sys.types AS t ON q.user_type_id = t.user_type_id
            WHERE s.name = {SchemaParameter} AND q.is_ms_shipped = 0
            ORDER BY q.name;
            """;
    }

    /// <summary>Synonyms in a schema; detail is the base object name. Requires <see cref="SchemaParameter"/>.</summary>
    public static string ListSynonyms(string database, string schema)
    {
        RequireName(schema);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT s.name, y.name, CONVERT(nvarchar(128), NULL), y.type_desc, CONVERT(bit, 0), y.base_object_name, CONVERT(bit, 0)
            FROM {db}.sys.synonyms AS y
            INNER JOIN {db}.sys.schemas AS s ON y.schema_id = s.schema_id
            WHERE s.name = {SchemaParameter} AND y.is_ms_shipped = 0
            ORDER BY y.name;
            """;
    }

    /// <summary>
    /// One row of counts for the object overview: primary keys, unique constraints, foreign keys, check constraints,
    /// default constraints, triggers, named indexes. Requires <see cref="SchemaParameter"/> and <see cref="TableParameter"/>.
    /// </summary>
    public static string TableOverviewCounts(string database, string schema, string table)
    {
        RequireName(schema);
        RequireName(table);
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT
              (SELECT CONVERT(int, COUNT(*)) FROM {db}.sys.key_constraints AS k WHERE k.parent_object_id = o.object_id AND k.type = 'PK'),
              (SELECT CONVERT(int, COUNT(*)) FROM {db}.sys.key_constraints AS k WHERE k.parent_object_id = o.object_id AND k.type = 'UQ'),
              (SELECT CONVERT(int, COUNT(*)) FROM {db}.sys.foreign_keys AS f WHERE f.parent_object_id = o.object_id),
              (SELECT CONVERT(int, COUNT(*)) FROM {db}.sys.check_constraints AS c WHERE c.parent_object_id = o.object_id),
              (SELECT CONVERT(int, COUNT(*)) FROM {db}.sys.default_constraints AS d WHERE d.parent_object_id = o.object_id),
              (SELECT CONVERT(int, COUNT(*)) FROM {db}.sys.triggers AS t WHERE t.parent_id = o.object_id AND t.parent_class = 1),
              (SELECT CONVERT(int, COUNT(*)) FROM {db}.sys.indexes AS i
                 WHERE i.object_id = o.object_id AND i.index_id > 0 AND i.name IS NOT NULL AND i.is_hypothetical = 0)
            FROM {db}.sys.objects AS o
            INNER JOIN {db}.sys.schemas AS s ON o.schema_id = s.schema_id
            WHERE s.name = {SchemaParameter} AND o.name = {TableParameter} AND o.type IN ('U', 'V');
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
