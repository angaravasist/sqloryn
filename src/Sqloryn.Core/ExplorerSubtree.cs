namespace Sqloryn.Core;

/// <summary>Clears one explorer node and its descendants. Parents and siblings are not visited.</summary>
public static class ExplorerSubtree
{
    public static void Invalidate<T>(T node, Func<T, IList<T>> children, Action<T> markUnloaded)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(markUnloaded);
        var list = children(node);
        foreach (var child in list.ToArray())
            Invalidate(child, children, markUnloaded);
        markUnloaded(node);
        list.Clear();
    }
}
