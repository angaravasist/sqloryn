namespace Sqloryn.Core;

/// <summary>
/// Object Explorer load orchestration with ownership checks. Expand/browse never executes SQL into the editor.
/// </summary>
public static class ObjectExplorerService
{
    public enum NodeKind
    {
        Database,
        Schema,
        Table,
        View,
        Procedure,
        Function,
        Key,
        Index,
        Column,
        Placeholder,
        ForeignKey,
        Constraint,
        Trigger,
        Sequence,
        Synonym
    }

    /// <summary>A static folder under a schema. <see cref="FolderKind"/>/<see cref="ChildKind"/> are the UI tree kinds.</summary>
    public sealed record SchemaFolder(string Title, string FolderKind, string ChildKind);

    /// <summary>Folders shown under every schema, in display order. Their contents load only when expanded.</summary>
    public static IReadOnlyList<SchemaFolder> SchemaFolders { get; } =
    [
        new("Tables", "tables", "table"),
        new("Views", "views", "view"),
        new("Keys", "keys", "key"),
        new("Indexes", "indexes", "index"),
        new("Programmability · Procedures", "procedures", "procedure"),
        new("Programmability · Functions", "functions", "function"),
        new("Sequences", "sequences", "sequence"),
        new("Synonyms", "synonyms", "synonym")
    ];

    /// <summary>Folders shown directly under a database, before schemas. Contents load only when expanded.</summary>
    public static IReadOnlyList<SchemaFolder> DatabaseFolders { get; } =
    [
        new("User-defined types", "types", "usertype")
    ];

    static readonly SchemaFolder[] TableFolderList =
    [
        new("Columns", "columns", "column"),
        new("Foreign Keys", "foreignkeys", "foreignkey"),
        new("Constraints (check · default)", "constraints", "constraint"),
        new("Triggers", "triggers", "trigger")
    ];

    static readonly SchemaFolder[] ViewFolderList = [TableFolderList[0], TableFolderList[3]];

    /// <summary>
    /// Folders shown under a table (columns, foreign keys, constraints, triggers) or view (columns, triggers).
    /// Contents load only when a folder expands. <paramref name="objectKind"/> is the UI tree kind: "table" or "view".
    /// </summary>
    public static IReadOnlyList<SchemaFolder> TableFolders(string objectKind) => objectKind switch
    {
        "table" => TableFolderList,
        "view" => ViewFolderList,
        _ => throw new ArgumentException("Only tables and views have object folders.", nameof(objectKind))
    };

    public sealed class Node
    {
        public required string Title { get; init; }
        public required NodeKind Kind { get; init; }
        public string? Database { get; init; }
        public string? Schema { get; init; }
        public string? ObjectName { get; init; }
        public List<Node> Children { get; } = [];
        public bool Loaded { get; set; }
    }

    /// <summary>
    /// Loads children for expansion. Caller supplies delayed work; stale bindings are rejected.
    /// </summary>
    public static async Task<bool> ExpandAsync(
        Node node,
        OperationBinding binding,
        Func<OperationBinding, bool> stillCurrent,
        Func<Node, CancellationToken, Task> loadChildren,
        CancellationToken cancellationToken = default)
    {
        if (node.Loaded || node.Kind is NodeKind.Column or NodeKind.Placeholder)
            return false;
        await loadChildren(node, cancellationToken).ConfigureAwait(false);
        if (!stillCurrent(binding))
        {
            node.Children.Clear();
            node.Loaded = false;
            return false;
        }
        node.Loaded = true;
        return true;
    }

    public static string BuildSelectScript(Node node)
    {
        if (node.Kind is not (NodeKind.Table or NodeKind.View))
            throw new InvalidOperationException("Only tables and views can be scripted.");
        if (node.Database is null || node.Schema is null || node.ObjectName is null)
            throw new InvalidOperationException("Incomplete object identity.");
        return SqlIdentity.ScriptSelectTop(node.Database, node.Schema, node.ObjectName);
    }
}
