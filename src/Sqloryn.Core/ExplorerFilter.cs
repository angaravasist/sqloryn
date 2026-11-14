namespace Sqloryn.Core;

/// <summary>Result of filtering a loaded explorer tree. Only loaded nodes are searched; no SQL runs.</summary>
public sealed class ExplorerFilterResult<T> where T : class
{
    readonly HashSet<T>? _visible;
    readonly HashSet<T>? _ancestors;

    internal ExplorerFilterResult(HashSet<T>? visible, HashSet<T>? ancestors, int matchCount)
    {
        _visible = visible;
        _ancestors = ancestors;
        MatchCount = matchCount;
    }

    /// <summary>Nodes whose own label matched (ancestors shown only for context are not counted).</summary>
    public int MatchCount { get; }

    /// <summary>True when the filter is blank, so everything is visible.</summary>
    public bool IsBlank => _visible is null;

    public bool IsVisible(T node) => _visible is null || _visible.Contains(node);

    /// <summary>True when a descendant matched, so the UI may expand this node to reveal it.</summary>
    public bool HasMatchingDescendant(T node) => _ancestors is not null && _ancestors.Contains(node);
}

/// <summary>
/// Client-side search over already-loaded Object Explorer labels: case-insensitive, whitespace-separated tokens that
/// must all appear (literal text, never a pattern). A matching node keeps its loaded subtree visible; non-matching
/// ancestors of a match stay visible for context.
/// </summary>
public static class ExplorerFilter
{
    public static string? Normalize(string? query)
    {
        var trimmed = query?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static bool Matches(string title, string? query)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (Normalize(query) is not { } q)
            return true;
        foreach (var token in q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!title.Contains(token, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    public static ExplorerFilterResult<T> Evaluate<T>(
        IEnumerable<T> roots,
        Func<T, IEnumerable<T>> children,
        Func<T, string> title,
        string? query) where T : class
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(title);
        if (Normalize(query) is null)
            return new ExplorerFilterResult<T>(null, null, 0);

        var visible = new HashSet<T>(ReferenceEqualityComparer.Instance);
        var ancestors = new HashSet<T>(ReferenceEqualityComparer.Instance);
        var matches = 0;

        void ShowSubtree(T node)
        {
            visible.Add(node);
            foreach (var child in children(node))
                ShowSubtree(child);
        }

        bool Visit(T node)
        {
            if (Matches(title(node), query))
            {
                matches++;
                // Descendants may match too; count them, then show the whole loaded subtree.
                var descendantMatch = false;
                foreach (var child in children(node))
                    descendantMatch |= Visit(child);
                if (descendantMatch)
                    ancestors.Add(node);
                ShowSubtree(node);
                return true;
            }
            var any = false;
            foreach (var child in children(node))
                any |= Visit(child);
            if (any)
            {
                visible.Add(node);
                ancestors.Add(node);
            }
            return any;
        }

        foreach (var root in roots)
            Visit(root);
        return new ExplorerFilterResult<T>(visible, ancestors, matches);
    }

    /// <summary>One-line text for an active filter that matched nothing.</summary>
    public static string EmptyStateText(string query)
    {
        var line = SchemaObjectCatalog.OneLine(query.Trim());
        if (line.Length > 60)
            line = line[..60] + "…";
        return $"No loaded items match “{line}”. Only expanded (already loaded) nodes are searched; expand a folder to include its items.";
    }

    /// <summary>
    /// Makes <paramref name="shown"/> equal to the visible subset of <paramref name="all"/>, in order, with the fewest
    /// changes so retained items (and the UI containers holding their expansion state) are not recreated.
    /// <paramref name="shown"/> must already be an ordered subsequence of <paramref name="all"/>.
    /// </summary>
    public static void SyncShown<T>(IList<T> shown, IReadOnlyList<T> all, Func<T, bool> visible) where T : class
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(visible);
        var i = 0;
        foreach (var item in all)
        {
            var inPlace = i < shown.Count && ReferenceEquals(shown[i], item);
            if (visible(item))
            {
                if (!inPlace)
                    shown.Insert(i, item);
                i++;
            }
            else if (inPlace)
            {
                shown.RemoveAt(i);
            }
        }
        while (shown.Count > i)
            shown.RemoveAt(shown.Count - 1);
    }
}
