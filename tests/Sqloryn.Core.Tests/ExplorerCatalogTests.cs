using Sqloryn.Core;

/// <summary>
/// WS2 Object Explorer: catalog projection, truncation honesty, denial mapping, filter helper,
/// object overview and App wiring source scans. Component-only: no SQL Server connection.
/// </summary>
static class ExplorerCatalogTests
{
    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"FAIL {name}");
    }

    static void Pass(string name) => Console.WriteLine($"PASS {name}");

    static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"FAIL {name}");
    }

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SQLoryn.slnx")))
                return d.FullName;
        throw new Exception("FAIL repo root (SQLoryn.slnx) not found from test base directory");
    }

    sealed class N(string title, params N[] children)
    {
        public string Title { get; } = title;
        public IReadOnlyList<N> Children { get; } = children;
    }

    public static void Run()
    {
        CatalogSql();
        Projection();
        FailureMapping();
        Folders();
        Filter();
        Overview();
        AppWiring();
    }

    static void CatalogSql()
    {
        var fk = MetadataCatalog.ListForeignKeys("My]Db", "dbo", "Ord]ers");
        Check(fk.Contains("[My]]Db].sys.foreign_keys", StringComparison.Ordinal) &&
              fk.Contains("@schema", StringComparison.Ordinal) && fk.Contains("@table", StringComparison.Ordinal) &&
              !fk.Contains("Ord]ers", StringComparison.Ordinal) && !fk.Contains("'dbo'", StringComparison.Ordinal), "explorer-sql-foreign-keys-parameterised");
        var constraints = MetadataCatalog.ListConstraints("d", "s", "t");
        Check(constraints.Contains("sys.check_constraints", StringComparison.Ordinal) &&
              constraints.Contains("sys.default_constraints", StringComparison.Ordinal) &&
              constraints.Contains("@table", StringComparison.Ordinal), "explorer-sql-constraints");
        var triggers = MetadataCatalog.ListTriggers("d", "s", "t");
        Check(triggers.Contains("sys.triggers", StringComparison.Ordinal) && triggers.Contains("parent_class = 1", StringComparison.Ordinal) &&
              triggers.Contains("@table", StringComparison.Ordinal), "explorer-sql-triggers-table-scoped");
        var sequences = MetadataCatalog.ListSequences("d", "s");
        Check(sequences.Contains("sys.sequences", StringComparison.Ordinal) && sequences.Contains("@schema", StringComparison.Ordinal) &&
              !sequences.Contains("@table", StringComparison.Ordinal), "explorer-sql-sequences-schema-scoped");
        var synonyms = MetadataCatalog.ListSynonyms("d", "s");
        Check(synonyms.Contains("sys.synonyms", StringComparison.Ordinal) && synonyms.Contains("base_object_name", StringComparison.Ordinal) &&
              !synonyms.Contains("@table", StringComparison.Ordinal), "explorer-sql-synonyms-schema-scoped");
        var counts = MetadataCatalog.TableOverviewCounts("d", "s", "t");
        Check(counts.Contains("sys.key_constraints", StringComparison.Ordinal) && counts.Contains("sys.foreign_keys", StringComparison.Ordinal) &&
              counts.Contains("sys.indexes", StringComparison.Ordinal) && counts.Contains("@table", StringComparison.Ordinal), "explorer-sql-overview-counts");

        foreach (var sql in new[] { fk, constraints, triggers, sequences, synonyms, counts })
            Check(!sql.Contains("USE ", StringComparison.Ordinal), "explorer-sql-no-use");
        Throws<ArgumentException>(() => MetadataCatalog.ListForeignKeys("", "dbo", "t"), "explorer-sql-bad-db");
        Throws<ArgumentException>(() => MetadataCatalog.ListTriggers("d", "", "t"), "explorer-sql-bad-schema");
        Throws<ArgumentException>(() => MetadataCatalog.ListConstraints("d", "s", ""), "explorer-sql-bad-table");
        Throws<ArgumentException>(() => MetadataCatalog.ListSynonyms("d", "bad\0schema"), "explorer-sql-nul-schema");
        Pass("explorer-metadata-sql");
    }

    static RawRows Raw(bool truncated, bool byteBudget, params object?[][] rows)
        => new(["schema", "name", "parent", "type_desc", "is_unique", "detail", "is_disabled"],
            ["nvarchar", "nvarchar", "nvarchar", "nvarchar", "bit", "nvarchar", "bit"], rows, truncated, byteBudget);

    static void Projection()
    {
        var raw = Raw(true, false,
            ["dbo", "FK_Orders_Cust", "Orders", "FOREIGN_KEY_CONSTRAINT", false, "dbo.Customers", false],
            ["dbo", "CK_Qty", "Orders", "CHECK_CONSTRAINT", false, "([Qty]>(0))", true],
            ["dbo", "DF_Orders_Created", "Orders", "DEFAULT_CONSTRAINT", false, "(sysutcdatetime())", false],
            ["dbo", "TR_Orders\nAudit", "Orders", "SQL_TRIGGER", false, "AFTER", false],
            ["dbo", "TR_Orders_Inst", "Orders", "SQL_TRIGGER", false, "INSTEAD OF", true],
            ["dbo", "seq_Order", null, "SEQUENCE_OBJECT", false, "bigint · start 1 · increment 1", false],
            ["dbo", "syn_Cust", null, "SYNONYM", false, "[Other].[dbo].[Customers]", false]);
        var items = SchemaObjectCatalog.Parse(raw);
        Check(items.Count == 7 && items[0].Detail == "dbo.Customers" && items[1].IsDisabled && !items[0].IsDisabled && items[5].Parent is null, "explorer-parse-detail-and-disabled");

        Check(SchemaObjectCatalog.Title("foreignkey", items[0]) == "FK_Orders_Cust → dbo.Customers", "explorer-title-foreignkey");
        Check(SchemaObjectCatalog.Title("constraint", items[1]) == "CK_Qty · check · disabled · ([Qty]>(0))", "explorer-title-check-disabled");
        Check(SchemaObjectCatalog.Title("constraint", items[2]) == "DF_Orders_Created · default · (sysutcdatetime())", "explorer-title-default");
        var trig = SchemaObjectCatalog.Title("trigger", items[3]);
        Check(!trig.Contains('\n') && trig.EndsWith("· after", StringComparison.Ordinal), "explorer-title-trigger-sanitised");
        Check(SchemaObjectCatalog.Title("trigger", items[4]) == "TR_Orders_Inst · instead of · disabled", "explorer-title-trigger-instead-of");
        Check(SchemaObjectCatalog.Title("sequence", items[5]) == "seq_Order · bigint · start 1 · increment 1", "explorer-title-sequence");
        Check(SchemaObjectCatalog.Title("synonym", items[6]) == "syn_Cust → [Other].[dbo].[Customers]", "explorer-title-synonym");

        // Hostile / oversized detail is single-line and bounded in the title; full text stays in the description.
        var longDef = "(" + new string('x', 600) + ")\r\nDROP TABLE z;--";
        var hostile = new SchemaObject("CK_Big", "T", "CHECK_CONSTRAINT", false, longDef, false);
        var hostileTitle = SchemaObjectCatalog.Title("constraint", hostile);
        Check(!hostileTitle.Contains('\n') && !hostileTitle.Contains('\r') && hostileTitle.Length < 220 && hostileTitle.EndsWith('…'), "explorer-title-detail-bounded");
        Check(SchemaObjectCatalog.Describe("constraint", hostile).Contains("Definition", StringComparison.Ordinal), "explorer-describe-constraint");

        // Omitted (oversized) cell is reported honestly, never shown as an empty definition.
        var omitted = Raw(false, false,
            ["dbo", "CK_Huge", "T", "CHECK_CONSTRAINT", false, new RawCellOmitted("oversized text; not loaded", null, 99999), false]);
        var omittedItem = SchemaObjectCatalog.Parse(omitted).Single();
        Check(omittedItem.Detail is not null && omittedItem.Detail.Contains("not loaded", StringComparison.OrdinalIgnoreCase), "explorer-parse-omitted-detail-honest");

        // Short legacy rows (4/5 columns) still parse.
        var legacy = new RawRows(["a", "b", "c", "d"], ["x", "x", "x", "x"], [["dbo", "PK", "T", "PRIMARY_KEY_CONSTRAINT"]], false);
        Check(SchemaObjectCatalog.Parse(legacy).Single().Detail is null, "explorer-parse-legacy-rows");

        // Truncation honesty for the new listings (row cap and byte budget).
        var rowCap = CatalogTruncation.Describe(items.Count, raw.Truncated, raw.ByteBudgetExhausted);
        Check(rowCap is not null && rowCap.Contains("7") && rowCap.Contains("More items exist", StringComparison.Ordinal), "explorer-truncation-row-cap");
        var budget = CatalogTruncation.Describe(1, false || true, true);
        Check(budget is not null && budget.Contains("budget", StringComparison.OrdinalIgnoreCase), "explorer-truncation-byte-budget");
        Check(CatalogTruncation.Describe(items.Count, false, false) is null, "explorer-truncation-none-when-complete");
        Pass("explorer-catalog-projection");
    }

    sealed class PermissionDenied() : Exception("The SELECT permission was denied on the object 'x', database 'y', schema 'dbo'.");

    static void FailureMapping()
    {
        foreach (var number in new[] { 229, 230, 262, 297, 300, 916 })
        {
            var f = CatalogFailure.From(new ProviderErrorException(number, "The SELECT permission was denied on the object 'secret'."), "foreign keys");
            Check(f.Kind == CatalogFailureKind.Denied && f.Number == number && f.Text.StartsWith("Denied:", StringComparison.Ordinal) &&
                  f.Text.Contains("foreign keys", StringComparison.Ordinal) && f.Text.Contains(number.ToString(), StringComparison.Ordinal), "explorer-denied-" + number);
        }
        var generic = CatalogFailure.From(new ProviderErrorException(208, "Invalid object name 'x'."), "triggers");
        Check(generic.Kind == CatalogFailureKind.Error && generic.Text.StartsWith("Error:", StringComparison.Ordinal) &&
              generic.Text.Contains("Invalid object name", StringComparison.Ordinal), "explorer-error-mapped");
        var plain = CatalogFailure.From(new InvalidOperationException("boom"), "sequences");
        Check(plain.Kind == CatalogFailureKind.Error && plain.Number == 0 && plain.Text.Contains("boom", StringComparison.Ordinal), "explorer-error-non-sql");
        var cancelled = CatalogFailure.From(new OperationCanceledException(), "synonyms");
        Check(cancelled.Kind == CatalogFailureKind.Cancelled && cancelled.Text.Contains("Cancelled", StringComparison.Ordinal), "explorer-cancelled-distinct");
        Check(CatalogFailure.From(new TaskCanceledException(), "x").Kind == CatalogFailureKind.Cancelled, "explorer-task-cancelled-distinct");

        var hostile = CatalogFailure.From(new ProviderErrorException(2812, "Could not find\nstored procedure 'x'.\r\n" + new string('y', 900)), "bad\nname");
        Check(!hostile.Text.Contains('\n') && !hostile.Text.Contains('\r') && hostile.Text.Length < 400, "explorer-error-one-line-bounded");
        var secret = CatalogFailure.From(new ProviderErrorException(18456, "Login failed. Password=hunter2;Server=x"), "tables");
        Check(!secret.Text.Contains("hunter2", StringComparison.Ordinal), "explorer-error-redacted");
        Check(CatalogFailure.From(new PermissionDenied(), "x").Kind == CatalogFailureKind.Error, "explorer-denial-requires-number");
        Pass("explorer-denial-mapping");
    }

    static void Folders()
    {
        var schema = ObjectExplorerService.SchemaFolders;
        foreach (var kind in new[] { "tables", "views", "keys", "indexes", "procedures", "functions", "sequences", "synonyms" })
            Check(schema.Any(f => f.FolderKind == kind), "explorer-schema-folder-" + kind);
        Check(schema.Single(f => f.FolderKind == "sequences").ChildKind == "sequence" &&
              schema.Single(f => f.FolderKind == "synonyms").ChildKind == "synonym", "explorer-schema-folder-child-kinds");

        var tableFolders = ObjectExplorerService.TableFolders("table");
        Check(tableFolders.Select(f => f.FolderKind).SequenceEqual(["columns", "foreignkeys", "constraints", "triggers"]), "explorer-table-folders");
        Check(tableFolders.Single(f => f.FolderKind == "foreignkeys").ChildKind == "foreignkey" &&
              tableFolders.Single(f => f.FolderKind == "constraints").ChildKind == "constraint" &&
              tableFolders.Single(f => f.FolderKind == "triggers").ChildKind == "trigger", "explorer-table-folder-child-kinds");
        var viewFolders = ObjectExplorerService.TableFolders("view");
        Check(viewFolders.Select(f => f.FolderKind).SequenceEqual(["columns", "triggers"]), "explorer-view-folders");
        Throws<ArgumentException>(() => ObjectExplorerService.TableFolders("procedure"), "explorer-table-folders-bad-kind");

        Check(ObjectExplorerService.NodeKind.Sequence != ObjectExplorerService.NodeKind.Synonym &&
              ObjectExplorerService.NodeKind.Trigger != ObjectExplorerService.NodeKind.Constraint &&
              ObjectExplorerService.NodeKind.ForeignKey != ObjectExplorerService.NodeKind.Key, "explorer-node-kinds");
        Pass("explorer-explorer-folders");
    }

    static void Filter()
    {
        Check(ExplorerFilter.Normalize("  ") is null && ExplorerFilter.Normalize(null) is null && ExplorerFilter.Normalize("  ab ") == "ab", "explorer-filter-normalize");
        Check(ExplorerFilter.Matches("dbo.Orders · primary key", "ORDERS"), "explorer-filter-case-insensitive");
        Check(ExplorerFilter.Matches("dbo.Orders · primary key", "orders key"), "explorer-filter-all-tokens");
        Check(!ExplorerFilter.Matches("dbo.Orders", "orders key"), "explorer-filter-token-missing");
        Check(ExplorerFilter.Matches("anything", null) && ExplorerFilter.Matches("anything", ""), "explorer-filter-blank-matches-all");
        Check(ExplorerFilter.Matches("a]b", "]"), "explorer-filter-literal-not-regex");
        Check(!ExplorerFilter.Matches("abc", ".*"), "explorer-filter-no-regex");

        var orders = new N("Orders", new N("Id : int"), new N("Total : money"));
        var customers = new N("Customers", new N("Name : nvarchar(50)"));
        var tables = new N("Tables", orders, customers);
        var views = new N("Views", new N("vOrders"));
        var dbo = new N("dbo", tables, views);
        var db = new N("Sales", dbo);
        var roots = new[] { db };

        var hit = ExplorerFilter.Evaluate(roots, n => n.Children, n => n.Title, "total");
        Check(hit.MatchCount == 1 && hit.IsVisible(db) && hit.IsVisible(dbo) && hit.IsVisible(tables) && hit.IsVisible(orders) &&
              !hit.IsVisible(customers) && !hit.IsVisible(views), "explorer-filter-shows-ancestors-hides-rest");
        Check(hit.HasMatchingDescendant(db) && hit.HasMatchingDescendant(tables) && hit.HasMatchingDescendant(orders) &&
              !hit.HasMatchingDescendant(customers), "explorer-filter-marks-ancestors-for-expansion");
        var many = ExplorerFilter.Evaluate(roots, n => n.Children, n => n.Title, "orders");
        Check(many.MatchCount == 2 && many.IsVisible(orders) && many.IsVisible(orders.Children[0]) && many.IsVisible(views.Children[0]) && !many.IsVisible(customers), "explorer-filter-matching-parent-keeps-subtree");
        var none = ExplorerFilter.Evaluate(roots, n => n.Children, n => n.Title, "zzz");
        Check(none.MatchCount == 0 && !none.IsVisible(db), "explorer-filter-no-match");
        var empty = ExplorerFilter.EmptyStateText("zzz");
        Check(empty.Contains("zzz", StringComparison.Ordinal) && empty.Contains("expanded", StringComparison.OrdinalIgnoreCase), "explorer-filter-empty-state-text");
        Check(!ExplorerFilter.EmptyStateText("a\nb").Contains('\n'), "explorer-filter-empty-state-one-line");
        var blank = ExplorerFilter.Evaluate(roots, n => n.Children, n => n.Title, "  ");
        Check(blank.IsVisible(customers) && blank.IsVisible(db), "explorer-filter-blank-everything-visible");

        // Minimal-diff sync keeps retained items in place (tree expansion state lives on retained containers).
        var all = new[] { "a", "b", "c", "d" };
        var shown = new List<string>(all);
        ExplorerFilter.SyncShown(shown, all, x => x is "a" or "c");
        Check(shown.SequenceEqual(["a", "c"]), "explorer-sync-hide");
        ExplorerFilter.SyncShown(shown, all, x => x is "a" or "b" or "c" or "d");
        Check(shown.SequenceEqual(all), "explorer-sync-show-all-order");
        ExplorerFilter.SyncShown(shown, all, _ => false);
        Check(shown.Count == 0, "explorer-sync-hide-all");
        ExplorerFilter.SyncShown(shown, all, x => x == "d");
        Check(shown.SequenceEqual(["d"]), "explorer-sync-single");
        Pass("explorer-explorer-filter");
    }

    static void Overview()
    {
        var counts = ObjectCounts.Parse(new RawRows(
            ["pk", "uq", "fk", "ck", "df", "tr", "ix"], ["int", "int", "int", "int", "int", "int", "int"],
            [[1, 2, 3, 4, 5, 6, 7]], false));
        Check(counts is { PrimaryKeys: 1, UniqueKeys: 2, ForeignKeys: 3, Checks: 4, Defaults: 5, Triggers: 6, Indexes: 7 } && counts.KeyCount == 6, "explorer-overview-counts-parse");
        Check(ObjectCounts.Parse(new RawRows(["a"], ["int"], [], false)) is null, "explorer-overview-counts-empty");

        var cols = new List<OverviewColumn>
        {
            new("Id", "int", false, true, false),
            new("Note", "nvarchar(50)", true, false, false),
            new("Total", "money", true, false, true)
        };
        var text = ObjectOverviewFormatter.Format("Table", "[Sales].[dbo].[Orders]", cols, false, ["Id"], counts);
        Check(text.Contains("[Sales].[dbo].[Orders]", StringComparison.Ordinal) && text.Contains("Columns", StringComparison.Ordinal) &&
              text.Contains("3 (1 identity · 2 nullable · 1 computed)", StringComparison.Ordinal), "explorer-overview-column-summary");
        Check(text.Contains("PK (Id)", StringComparison.Ordinal) && text.Contains("2 unique", StringComparison.Ordinal) && text.Contains("3 foreign", StringComparison.Ordinal), "explorer-overview-keys");
        Check(text.Contains("Id : int · identity · NOT NULL", StringComparison.Ordinal), "explorer-overview-column-lines");
        Check(!text.Contains('\r'), "explorer-overview-no-cr");

        var noPk = ObjectOverviewFormatter.Format("Table", "t", cols, false, [], new ObjectCounts(0, 0, 0, 0, 0, 0, 0));
        Check(noPk.Contains("no primary key", StringComparison.OrdinalIgnoreCase), "explorer-overview-no-pk");
        var noCounts = ObjectOverviewFormatter.Format("View", "v", cols, false, [], null);
        Check(noCounts.Contains("not available", StringComparison.OrdinalIgnoreCase), "explorer-overview-counts-unavailable");

        var many = Enumerable.Range(0, 40).Select(i => new OverviewColumn("c" + i, "int", true, false, false)).ToList();
        var bounded = ObjectOverviewFormatter.Format("Table", "t", many, false, [], counts, maxColumnLines: 12);
        Check(bounded.Contains("c11 : int", StringComparison.Ordinal) && !bounded.Contains("c12 : int", StringComparison.Ordinal) &&
              bounded.Contains("28 more columns", StringComparison.Ordinal), "explorer-overview-bounded-column-lines");
        var truncated = ObjectOverviewFormatter.Format("Table", "t", many, true, [], counts, maxColumnLines: 12);
        Check(truncated.Contains("40+", StringComparison.Ordinal) && truncated.Contains("truncated", StringComparison.OrdinalIgnoreCase), "explorer-overview-truncated-honest");

        var hostile = ObjectOverviewFormatter.Format("Table", "t\nx", [new OverviewColumn("a\nb", "int", true, false, false)], false, ["k\nk"], null);
        Check(hostile.Split('\n').All(l => !l.Contains('\r')) && !hostile.Contains("a\nb", StringComparison.Ordinal) && !hostile.Contains("t\nx", StringComparison.Ordinal), "explorer-overview-hostile-names-one-line");
        Pass("explorer-object-overview");
    }

    static void AppWiring()
    {
        var appDir = Path.Combine(FindRepoRoot(), "src", "Sqloryn.App");
        var xaml = File.ReadAllText(Path.Combine(appDir, "MainWindow.axaml"));
        var doc = System.Xml.Linq.XDocument.Load(Path.Combine(appDir, "MainWindow.axaml"));
        string? Auto(string name) => doc.Descendants().FirstOrDefault(e => e.Attribute("Name")?.Value == name)?.Attribute("AutomationProperties.Name")?.Value;
        Check(Auto("ExplorerFilterBox") == "Filter object explorer", "explorer-app-filter-box-automation-name");
        Check(doc.Descendants().Any(e => e.Attribute("Name")?.Value == "ExplorerFilterEmpty"), "explorer-app-filter-empty-state-control");
        Check(Auto("PropertiesBox") is not null, "explorer-app-inspector-automation-name");

        var main = File.ReadAllText(Path.Combine(appDir, "MainWindow.axaml.cs"));
        var controllerPath = Path.Combine(appDir, "ExplorerController.cs");
        Check(File.Exists(controllerPath), "explorer-app-explorer-controller-extracted");
        var controller = File.ReadAllText(controllerPath);
        Check(controller.Contains("CancellationTokenSource", StringComparison.Ordinal) && controller.Contains("_expansionCts?.Cancel()", StringComparison.Ordinal), "explorer-app-expansion-cancels-prior");
        Check(controller.Contains("CatalogFailure.From", StringComparison.Ordinal), "explorer-app-denial-mapped-to-node-text");
        Check(!main.Contains("CatalogClient.", StringComparison.Ordinal), "explorer-app-mainwindow-no-catalog-calls");
        Check(main.Split('\n').Length < 1471, "explorer-app-mainwindow-not-grown-endlessly");
        Check(File.ReadAllText(Path.Combine(appDir, "CatalogClient.cs")).Contains("ListForeignKeysAsync", StringComparison.Ordinal) &&
              File.ReadAllText(Path.Combine(appDir, "CatalogClient.cs")).Contains("ListSynonymsAsync", StringComparison.Ordinal), "explorer-app-catalog-client-extended");
        Check(xaml.Contains("ExplorerFilterBox", StringComparison.Ordinal), "explorer-app-xaml-filter-present");
        Pass("explorer-app-wiring-source-scan");
    }
}
