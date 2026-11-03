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
        Column,
        Placeholder
    }

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
