using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>
/// Object Explorer tree state and loading, kept out of the window code. Owns node mutation, lazy expansion (a new
/// expansion cancels the one before it), failure/denial node text, the client-side filter and the object overview load.
/// Catalog SQL stays in <see cref="CatalogClient"/>; this class never builds SQL.
/// </summary>
sealed class ExplorerController(Func<QueryTab, OperationBinding, bool> isCurrent, Action<string> setStatus)
{
    readonly List<ExplorerNode> _roots = [];
    // Not disposed: a CancellationTokenSource with no timer or wait handle holds no unmanaged resource, and a later
    // expansion may still call Cancel() on the previous source.
    CancellationTokenSource? _expansionCts;
    CancellationTokenSource? _overviewCts;
    string? _query;
    ExplorerFilterResult<ExplorerNode>? _filter;

    /// <summary>The roots the TreeView binds to (all roots unless a filter hides some).</summary>
    public ObservableCollection<ExplorerNode> ShownRoots { get; } = [];

    public bool HasRoots => _roots.Count > 0;

    public bool FilterActive => _query is not null;

    /// <summary>Raised after the tree or filter changed so the window can refresh the empty-state text.</summary>
    public event Action? StateChanged;

    // ---- Tree content ---------------------------------------------------------------------

    public void Clear()
    {
        CancelLoads();
        _roots.Clear();
        ShownRoots.Clear();
        _filter = null;
        StateChanged?.Invoke();
    }

    public void SetRoots(IEnumerable<ExplorerNode> roots)
    {
        _roots.Clear();
        _roots.AddRange(roots);
        Changed(null);
    }

    public static ExplorerNode WithPlaceholder(ExplorerNode node)
    {
        node.Children.Add(Placeholder(node));
        node.Shown.Add(node.Children[^1]);
        return node;
    }

    static ExplorerNode Placeholder(ExplorerNode owner, string title = "…", string? info = null)
        => new(title, "placeholder", owner.Database, owner.Schema, owner.ObjectName) { Info = info };

    /// <summary>Collapses a node back to its unloaded state so the next expansion reloads it.</summary>
    public void Reset(ExplorerNode node)
    {
        node.Loaded = false;
        SetChildren(node, [Placeholder(node)]);
    }

    void SetChildren(ExplorerNode node, IEnumerable<ExplorerNode> children)
    {
        node.Children.Clear();
        node.Children.AddRange(children);
        Changed(node);
    }

    /// <summary>The Tables folder for the node's schema, searched among the loaded nodes.</summary>
    public ExplorerNode? FindTablesFolder(ExplorerNode from)
    {
        IEnumerable<ExplorerNode> All(IEnumerable<ExplorerNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(All(n.Children)));
        return All(_roots).FirstOrDefault(n => n.Kind == "tables" &&
            string.Equals(n.Database, from.Database, StringComparison.Ordinal) &&
            string.Equals(n.Schema, from.Schema, StringComparison.Ordinal));
    }

    // ---- Filter ---------------------------------------------------------------------------

    /// <summary>Applies a label filter to the loaded nodes. Blank clears it. Returns the number of matching nodes.</summary>
    public int SetFilter(string? text)
    {
        _query = ExplorerFilter.Normalize(text);
        Changed(null);
        return _filter?.MatchCount ?? 0;
    }

    public int FilterMatchCount => _filter?.MatchCount ?? 0;

    public string? FilterQuery => _query;

    static string FilterTitle(ExplorerNode n)
        // "…" and "Loading…" placeholders carry no information; denial/error/truncation placeholders do.
        => n.Kind == "placeholder" && n.Info is null ? "" : n.Title;

    void Changed(ExplorerNode? changed)
    {
        if (_query is null)
        {
            _filter = null;
            if (changed is null)
                ExplorerFilter.SyncShown(ShownRoots, _roots, _ => true);
            else
                ExplorerFilter.SyncShown(changed.Shown, changed.Children, _ => true);
        }
        else
        {
            _filter = ExplorerFilter.Evaluate(_roots, n => n.Children, FilterTitle, _query);
            ApplyFilter();
        }
        StateChanged?.Invoke();
    }

    void ApplyFilter()
    {
        var filter = _filter!;
        ExplorerFilter.SyncShown(ShownRoots, _roots, filter.IsVisible);
        void Walk(ExplorerNode node)
        {
            ExplorerFilter.SyncShown(node.Shown, node.Children, filter.IsVisible);
            if (filter.HasMatchingDescendant(node))
                node.IsExpanded = true;
            foreach (var child in node.Children)
                Walk(child);
        }
        foreach (var root in _roots)
            Walk(root);
    }

    // ---- Expansion ------------------------------------------------------------------------

    /// <summary>Cancels any in-flight expansion and overview read (tab switch, refresh, disconnect).</summary>
    public void CancelLoads()
    {
        _expansionCts?.Cancel();
        _overviewCts?.Cancel();
    }

    /// <summary>
    /// Loads a node's children. A newer expansion cancels this one (the cancelled node shows a retry note). Results are
    /// discarded when the tab, session, database or generation changed while the read was in flight. A denied or failed
    /// read is shown as node text, never as an empty folder.
    /// </summary>
    public async Task ExpandAsync(ExplorerNode node, QueryTab tab)
    {
        if (node.Loaded || node.Loading || node.IsLeaf)
            return;
        var db = node.Database;
        if (db is null)
            return;
        _expansionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _expansionCts = cts;
        var session = tab.Session;
        var binding = tab.CaptureBinding();

        node.Loading = true;
        SetChildren(node, [Placeholder(node, "Loading…")]);
        (List<ExplorerNode> Children, string? Note) result;
        try
        {
            result = await FetchAsync(node, session, db, cts.Token);
        }
        catch (Exception ex)
        {
            node.Loading = false;
            var failure = CatalogFailure.From(ex, What(node));
            node.Loaded = false;
            SetChildren(node, [Placeholder(node, failure.Text, failure.Text)]);
            if (failure.Kind != CatalogFailureKind.Cancelled && isCurrent(tab, binding))
                setStatus("Object explorer: " + failure.Text);
            return;
        }
        node.Loading = false;

        if (!isCurrent(tab, binding))
        {
            Reset(node);
            setStatus("Object explorer load discarded (tab/session/database changed).");
            return;
        }
        var children = result.Children;
        if (children.Count == 0)
            children.Add(Placeholder(node, "(none visible)", "No items were returned. Items you lack VIEW DEFINITION on are hidden by SQL Server."));
        node.Info = result.Note;
        if (result.Note is not null)
        {
            // Never present a capped catalog read as the complete list.
            children.Add(Placeholder(node, "… (truncated)", result.Note));
            setStatus("Object explorer: " + result.Note);
        }
        node.Loaded = true;
        SetChildren(node, children);
    }

    static string What(ExplorerNode node) => node.Kind switch
    {
        "database" => "schemas in " + node.Database,
        "table" or "view" => "folders of " + node.ObjectName,
        _ => node.Title + (node.ObjectName is null ? "" : " of " + node.ObjectName)
    };

    static readonly IReadOnlyDictionary<string, string> ChildKinds = ObjectExplorerService.SchemaFolders
        .Concat(ObjectExplorerService.TableFolders("table"))
        .ToDictionary(f => f.FolderKind, f => f.ChildKind);

    static async Task<(List<ExplorerNode> Children, string? Note)> FetchAsync(
        ExplorerNode node, SqlSession session, string db, CancellationToken ct)
    {
        var schema = node.Schema;
        var table = node.ObjectName;
        switch (node.Kind)
        {
            case "database":
            {
                var schemas = await CatalogClient.ListSchemasAsync(session, db, ct);
                return (schemas.Items.Select(s => WithPlaceholder(new ExplorerNode(s, "schema", db, s))).ToList(), schemas.TruncationNote);
            }
            case "schema" when schema is not null:
                // Folders are static; each catalog query runs when its folder expands.
                return (ObjectExplorerService.SchemaFolders
                    .Select(f => WithPlaceholder(new ExplorerNode(f.Title, f.FolderKind, db, schema))).ToList(), null);
            case "tables" or "views" or "procedures" when schema is not null:
            {
                var names = node.Kind switch
                {
                    "tables" => await CatalogClient.ListTablesAsync(session, db, schema, ct),
                    "views" => await CatalogClient.ListViewsAsync(session, db, schema, ct),
                    _ => await CatalogClient.ListProceduresAsync(session, db, schema, ct)
                };
                var childKind = ChildKinds[node.Kind];
                return (names.Items.Select(n =>
                {
                    var child = new ExplorerNode(n, childKind, db, schema, n);
                    return childKind == "procedure" ? child : WithPlaceholder(child);
                }).ToList(), names.TruncationNote);
            }
            case "keys" or "indexes" or "functions" or "sequences" or "synonyms" when schema is not null:
            {
                var items = node.Kind switch
                {
                    "keys" => await CatalogClient.ListKeysAsync(session, db, schema, ct),
                    "indexes" => await CatalogClient.ListIndexesAsync(session, db, schema, ct),
                    "functions" => await CatalogClient.ListFunctionsAsync(session, db, schema, ct),
                    "sequences" => await CatalogClient.ListSequencesAsync(session, db, schema, ct),
                    _ => await CatalogClient.ListSynonymsAsync(session, db, schema, ct)
                };
                return (SchemaObjectNodes(items, ChildKinds[node.Kind], db, schema, table: null), items.TruncationNote);
            }
            case "table" or "view" when schema is not null && table is not null:
                return (ObjectExplorerService.TableFolders(node.Kind)
                    .Select(f => WithPlaceholder(new ExplorerNode(f.Title, f.FolderKind, db, schema, table))).ToList(), null);
            case "columns" when schema is not null && table is not null:
            {
                var columns = await CatalogClient.ListColumnsPageAsync(session, db, schema, table, ct);
                return (columns.Items.Select(c => new ExplorerNode(
                    $"{c.Name} : {c.TypeDisplay}{(c.IsNullable ? "" : " · NOT NULL")}{(c.IsIdentity ? " · identity" : "")}",
                    "column", db, schema, table)
                {
                    Info = $"Column   {c.Name}\nType     {c.TypeDisplay}\nNullable {(c.IsNullable ? "yes" : "no")}\nIdentity {(c.IsIdentity ? "yes" : "no")}"
                }).ToList(), columns.TruncationNote);
            }
            case "foreignkeys" or "constraints" or "triggers" when schema is not null && table is not null:
            {
                var items = node.Kind switch
                {
                    "foreignkeys" => await CatalogClient.ListForeignKeysAsync(session, db, schema, table, ct),
                    "constraints" => await CatalogClient.ListConstraintsAsync(session, db, schema, table, ct),
                    _ => await CatalogClient.ListTriggersAsync(session, db, schema, table, ct)
                };
                return (SchemaObjectNodes(items, ChildKinds[node.Kind], db, schema, table), items.TruncationNote);
            }
            default:
                return ([], null);
        }
    }

    static List<ExplorerNode> SchemaObjectNodes(
        CatalogListing<SchemaObject> items, string childKind, string db, string schema, string? table)
        => items.Items.Select(o => new ExplorerNode(SchemaObjectCatalog.Title(childKind, o), childKind, db, schema, table ?? o.Name)
        {
            Info = SchemaObjectCatalog.Describe(childKind, o)
        }).ToList();

    // ---- Object overview ------------------------------------------------------------------

    /// <summary>
    /// Loads the table/view overview for the inspector. Cancels any previous overview read. Returns null when the
    /// result is stale (selection or tab changed). A denied/failed read returns node-style text, never blank.
    /// </summary>
    public async Task<string?> LoadOverviewAsync(ExplorerNode node, QueryTab tab, Func<bool> stillSelected)
    {
        if (node.Kind is not ("table" or "view") || node.Database is null || node.Schema is null || node.ObjectName is null)
            return null;
        if (tab.IsExecuting || tab.Session.HasActiveCommand)
            return "Overview unavailable while a command is running on this tab.";
        _overviewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _overviewCts = cts;
        var binding = tab.CaptureBinding();
        string text;
        try
        {
            text = await CatalogClient.LoadOverviewAsync(
                tab.Session, node.Database, node.Schema, node.ObjectName, node.Kind == "table" ? "Table" : "View", cts.Token);
        }
        catch (Exception ex)
        {
            text = CatalogFailure.From(ex, "overview of " + node.ObjectName).Text;
            if (ex is OperationCanceledException)
                return null;
        }
        return cts.IsCancellationRequested || !isCurrent(tab, binding) || !stillSelected() ? null : text;
    }

    // ---- Properties text ------------------------------------------------------------------

    /// <summary>Compact multi-line description of a node for the properties panel.</summary>
    public static string Describe(ExplorerNode? node, string? server)
    {
        if (node is null)
            return "Select an object to see its properties.";
        var sb = new StringBuilder();
        void Line(string label, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                sb.Append(label.PadRight(10)).Append(SchemaObjectCatalog.OneLine(value)).Append('\n');
        }
        Line("Kind", node.Kind switch
        {
            "database" => "Database",
            "schema" => "Schema",
            "tables" => "Tables folder",
            "views" => "Views folder",
            "keys" => "Keys folder",
            "indexes" => "Indexes folder",
            "procedures" => "Procedures folder",
            "functions" => "Functions folder",
            "sequences" => "Sequences folder",
            "synonyms" => "Synonyms folder",
            "columns" => "Columns folder",
            "foreignkeys" => "Foreign keys folder",
            "constraints" => "Constraints folder",
            "triggers" => "Triggers folder",
            "table" => "Table",
            "view" => "View",
            "key" => "Key",
            "index" => "Index",
            "foreignkey" => "Foreign key",
            "constraint" => "Constraint",
            "trigger" => "Trigger",
            "sequence" => "Sequence",
            "synonym" => "Synonym",
            "procedure" => "Stored procedure",
            "function" => "Function",
            "column" => "Column",
            _ => "Item"
        });
        Line("Server", server);
        Line("Database", node.Database);
        Line("Schema", node.Schema);
        var named = node.Kind is "table" or "view" or "procedure" or "key" or "index" or "function" or "column" || node.IsFolder && node.ObjectName is not null;
        Line(node.Kind == "column" || node.IsFolder ? "Table" : "Name", named ? node.ObjectName : null);
        if (node.Kind is "table" or "view" && node.Database is not null && node.Schema is not null && node.ObjectName is not null)
            Line("Qualified", SqlIdentity.Qualify3(node.Database, node.Schema, node.ObjectName));
        var loaded = node.Children.Count(c => c.Kind != "placeholder").ToString(CultureInfo.InvariantCulture);
        if (node.IsFolder && node.Loaded)
            Line("Items", loaded);
        var text = sb.ToString();
        if (!string.IsNullOrEmpty(node.Info))
            text += "\n" + node.Info + "\n";
        return text.TrimEnd();
    }
}
