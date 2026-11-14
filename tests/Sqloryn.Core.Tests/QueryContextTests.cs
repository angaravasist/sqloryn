using Sqloryn.Core;

/// <summary>
/// Database context on the active tab, session ANSI settings,
/// running one GO batch and refreshing one Object Explorer subtree. No SQL Server connection.
/// </summary>
static class QueryContextTests
{
    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"FAIL {name}");
    }

    static void Pass(string name) => Console.WriteLine($"PASS {name}");

    public static async Task RunAsync()
    {
        await DatabaseSwitch();
        AnsiSettings();
        await OneBatch();
        SubtreeRefresh();
        Wiring();
        Pass("query-context");
    }

    static async Task DatabaseSwitch()
    {
        var active = new FakeDatabaseSession("login-a", "AppDb", ["AppDb", "OtherDb"]);
        var other = new FakeDatabaseSession("login-b", "KeepDb", ["KeepDb", "OtherDb"]);
        var outcome = await ActiveDatabaseSwitch.ApplyAsync(active, [other], "OtherDb");
        Check(outcome.Applied && outcome.Database == "OtherDb", "db-context-active-database");
        Check(active.Database == "OtherDb" && active.Changes.Count == 1 && active.Changes[0] == "OtherDb", "db-context-one-change-call");
        Check(other.Database == "KeepDb" && other.Changes.Count == 0 && other.ListCalls == 0, "db-context-other-tab-unchanged");
        Check(active.Login == "login-a" && other.Login == "login-b", "db-context-logins-untouched");
        Check(outcome.Message.Contains("not re-entered", StringComparison.Ordinal)
              && outcome.Message.Contains("Other query tabs", StringComparison.Ordinal), "db-context-message");

        var same = await ActiveDatabaseSwitch.ApplyAsync(active, [other], "otherdb");
        Check(same.Applied && active.Changes.Count == 1 && other.Database == "KeepDb", "db-context-same-database-is-noop");

        var missing = await ActiveDatabaseSwitch.ApplyAsync(active, [other], "Missing");
        Check(!missing.Applied && active.Database == "OtherDb" && other.Changes.Count == 0, "db-context-unknown-database-refused");
        Check(missing.Message.Contains("not in the databases", StringComparison.Ordinal), "db-context-unknown-explains");

        var offline = new FakeDatabaseSession("login-a", null, ["AppDb"], connected: false);
        var refused = await ActiveDatabaseSwitch.ApplyAsync(offline, [other], "AppDb");
        Check(!refused.Applied && offline.ListCalls == 0 && offline.Changes.Count == 0 && other.Database == "KeepDb", "db-context-disconnected-does-not-list");
        Check(refused.Message.Contains("Connect", StringComparison.Ordinal)
              && refused.Message.Contains("not requested", StringComparison.Ordinal), "db-context-disconnected-message");

        var failing = new FakeDatabaseSession("login-a", "AppDb", ["AppDb", "OtherDb"])
        {
            ChangeError = new InvalidOperationException("Refusing database change while a transaction is open.")
        };
        try
        {
            await ActiveDatabaseSwitch.ApplyAsync(failing, [other], "OtherDb");
            throw new Exception("FAIL db-context-transaction-should-throw");
        }
        catch (InvalidOperationException)
        {
            Check(failing.Database == "AppDb" && other.Database == "KeepDb", "db-context-failed-change-keeps-both");
        }

        var change = typeof(IDatabaseSession).GetMethod(nameof(IDatabaseSession.ChangeDatabaseAsync));
        Check(change is not null && change.GetParameters().Count(p => p.ParameterType == typeof(string)) == 1, "db-context-no-credential-parameter");
        Pass("db-context-active-database-switch");
    }

    static void AnsiSettings()
    {
        var settings = new QueryAnsiSettings(false, true, false, true);
        var sql = settings.ToSessionScript();
        Check(sql.Contains("SET ANSI_NULLS OFF;", StringComparison.Ordinal), "session-options-ansi-nulls");
        Check(sql.Contains("SET ANSI_PADDING ON;", StringComparison.Ordinal), "session-options-ansi-padding");
        Check(sql.Contains("SET QUOTED_IDENTIFIER OFF;", StringComparison.Ordinal), "session-options-quoted-identifier");
        Check(sql.Contains("SET ARITHABORT ON;", StringComparison.Ordinal), "session-options-arithabort");
        Check(sql.Contains("PRINT ", StringComparison.Ordinal), "session-options-print-for-messages");
        Check(sql.Contains("SESSIONPROPERTY(N'ANSI_NULLS')", StringComparison.Ordinal)
              && sql.Contains("SESSIONPROPERTY(N'ANSI_PADDING')", StringComparison.Ordinal)
              && sql.Contains("SESSIONPROPERTY(N'QUOTED_IDENTIFIER')", StringComparison.Ordinal)
              && sql.Contains("SESSIONPROPERTY(N'ARITHABORT')", StringComparison.Ordinal), "session-options-session-property");
        Check(!sql.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase), "session-options-script-has-no-timeout");
        Check(!sql.Contains("GO", StringComparison.OrdinalIgnoreCase), "session-options-single-batch");
        Check(QueryAnsiSettings.ScopeNote.Contains("SSMS", StringComparison.Ordinal)
              && QueryAnsiSettings.ScopeNote.Contains("command timeout", StringComparison.OrdinalIgnoreCase)
              && QueryAnsiSettings.ScopeNote.Contains("session", StringComparison.OrdinalIgnoreCase), "session-options-honest-scope");
        Pass("session-options-ansi-script");
    }

    static async Task OneBatch()
    {
        var script = "SELECT 1\nGO\nSELECT 2\nGO\nSELECT 3";
        Check(GoBatchSplitter.Choose(script, script.IndexOf("SELECT 2", StringComparison.Ordinal)).Text == "SELECT 2", "go-batch-caret-in-middle");
        Check(GoBatchSplitter.Choose(script, script.IndexOf("GO", StringComparison.Ordinal)).Text == "SELECT 1", "go-batch-caret-on-go");
        Check(GoBatchSplitter.Choose(script, script.Length).Text == "SELECT 3", "go-batch-caret-at-end");
        var blank = "SELECT 1\n\nGO\nSELECT 2";
        Check(GoBatchSplitter.Choose(blank, blank.IndexOf("\n\n", StringComparison.Ordinal) + 1).Text == "SELECT 1", "go-batch-caret-in-gap");
        Check(GoBatchSplitter.Choose("GO\nSELECT 1", 0).Text == "SELECT 1", "go-batch-leading-go");
        var quoted = "SELECT 'GO'\nGO\nSELECT 2";
        Check(GoBatchSplitter.Choose(quoted, 0).Text == "SELECT 'GO'", "go-batch-go-inside-string");
        Check(GoBatchSplitter.Choose(quoted, quoted.IndexOf("SELECT 2", StringComparison.Ordinal)).Text == "SELECT 2", "go-batch-after-string-go");

        var calls = new List<string>();
        var ran = await GoBatchExecution.ExecuteOneAsync(
            (sql, _) =>
            {
                calls.Add(sql);
                return Task.FromResult(Result(sql));
            },
            script,
            GoBatchExecution.ResolveCaret(script.Length, script.IndexOf("SELECT 2", StringComparison.Ordinal), 8));
        Check(calls.Count == 1 && calls[0] == "SELECT 2", "go-batch-executes-one-text");
        Check(ran.ExecutedSql == "SELECT 2", "go-batch-result-is-chosen-batch");

        var repeated = "SELECT 1\nGO 3\nSELECT 2";
        var plan = GoBatchExecution.Plan(repeated, 0);
        Check(plan.Sql == "SELECT 1" && plan.RepeatCount == 3 && plan.Count == 2, "go-batch-repeat-is-recorded");
        calls.Clear();
        await GoBatchExecution.ExecuteOneAsync((sql, _) =>
        {
            calls.Add(sql);
            return Task.FromResult(Result(sql));
        }, repeated, 0);
        Check(calls.Count == 1 && calls[0] == "SELECT 1", "go-batch-repeat-runs-once");
        Check(plan.Note.Contains("once", StringComparison.Ordinal)
              && plan.Note.Contains("not repeated", StringComparison.Ordinal)
              && plan.Note.Contains("Other batches", StringComparison.Ordinal), "go-batch-note");

        calls.Clear();
        try
        {
            await GoBatchExecution.ExecuteOneAsync((sql, _) =>
            {
                calls.Add(sql);
                return Task.FromResult(Result(sql));
            }, "SELECT 1\nGO 0\nSELECT 2", 0);
            throw new Exception("FAIL go-batch-malformed-should-reject");
        }
        catch (SqlScriptRejectedException)
        {
            Check(calls.Count == 0, "go-batch-malformed-executes-nothing");
        }

        var only = GoBatchExecution.Plan("SELECT 1", 3);
        Check(only.Sql == "SELECT 1" && only.Count == 1, "go-batch-no-go-is-one-batch");
        try
        {
            GoBatchExecution.Plan("GO\n  \nGO", 0);
            throw new Exception("FAIL go-batch-empty-should-refuse");
        }
        catch (InvalidOperationException)
        {
            Pass("go-batch-empty-refused");
        }

        Check(GoBatchExecution.ResolveCaret(10, 4, 0) == 10, "go-batch-caret-without-selection");
        Check(GoBatchExecution.ResolveCaret(10, 4, 3) == 4, "go-batch-selection-uses-its-start");
        Pass("go-batch-one-batch");
    }

    static void SubtreeRefresh()
    {
        var tables = Branch("tables", "T1", "T2");
        var views = Branch("views", "V1");
        var schema = Branch("schema");
        schema.Children.Add(tables);
        schema.Children.Add(views);
        var otherDb = Branch("other", "keep");
        var root = Branch("root");
        root.Children.Add(schema);
        root.Children.Add(otherDb);

        ExplorerSubtree.Invalidate(tables, n => n.Children, n => n.Loaded = false);

        Check(!tables.Loaded && tables.Children.Count == 0, "subtree-refresh-selected-cleared");
        Check(views.Loaded && views.Children.Count == 1 && views.Children[0].Name == "V1", "subtree-refresh-sibling-kept");
        Check(schema.Loaded && schema.Children.Count == 2 && ReferenceEquals(schema.Children[0], tables), "subtree-refresh-parent-kept");
        Check(otherDb.Loaded && otherDb.Children.Count == 1 && otherDb.Children[0].Name == "keep", "subtree-refresh-other-database-kept");
        Check(root.Children.Count == 2, "subtree-refresh-roots-kept");
        Pass("subtree-refresh-subtree");
    }

    static void Wiring()
    {
        var root = FindRepoRoot();
        var app = Path.Combine(root, "src", "Sqloryn.App");
        var xaml = File.ReadAllText(Path.Combine(app, "MainWindow.axaml"));
        var window = File.ReadAllText(Path.Combine(app, "MainWindow.axaml.cs"))
                     + File.ReadAllText(Path.Combine(app, "MainWindow.Session.cs"));
        var shell = File.ReadAllText(Path.Combine(app, "MainWindow.Shell.cs"));
        var explorer = File.ReadAllText(Path.Combine(app, "ExplorerController.cs"));
        var dialogXaml = File.ReadAllText(Path.Combine(app, "QueryOptionsWindow.axaml"));
        var dialog = File.ReadAllText(Path.Combine(app, "QueryOptionsWindow.axaml.cs"));

        Check(xaml.Contains("Name=\"DatabaseBox\"", StringComparison.Ordinal), "db-context-database-combo");
        Check(shell.Contains("Change _Database", StringComparison.Ordinal), "db-context-menu");
        Check(window.Contains("ActiveDatabaseSwitch.ApplyAsync", StringComparison.Ordinal), "db-context-uses-switch");
        var databaseHandler = MethodBody(window, "void OnDatabaseChanged");
        Check(databaseHandler.Contains("_active", StringComparison.Ordinal)
              && databaseHandler.Contains("ActiveDatabaseSwitch.ApplyAsync", StringComparison.Ordinal)
              && !databaseHandler.Contains("ConnectAsync", StringComparison.Ordinal), "db-context-active-tab-without-login");

        Check(shell.Contains("Session _ANSI Settings", StringComparison.Ordinal), "session-options-menu");
        Check(dialogXaml.Contains("ANSI_NULLS", StringComparison.Ordinal)
              && dialogXaml.Contains("ANSI_PADDING", StringComparison.Ordinal)
              && dialogXaml.Contains("QUOTED_IDENTIFIER", StringComparison.Ordinal)
              && dialogXaml.Contains("ARITHABORT", StringComparison.Ordinal), "session-options-dialog-options");
        Check(dialog.Contains("QueryAnsiSettings", StringComparison.Ordinal)
              && dialog.Contains("ToSessionScript", StringComparison.Ordinal)
              && dialog.Contains("ScopeNote", StringComparison.Ordinal), "session-options-dialog-previews-core-script");
        Check(!dialogXaml.Contains("timeout", StringComparison.OrdinalIgnoreCase), "session-options-dialog-has-no-timeout");
        var ansi = MethodBody(window, "void OnSessionAnsi");
        Check(ansi.Contains("QueryOptionsWindow", StringComparison.Ordinal)
              && ansi.Contains("_active", StringComparison.Ordinal)
              && ansi.Contains("ExecuteOnTabAsync", StringComparison.Ordinal), "session-options-apply-on-active-tab");

        Check(shell.Contains("Run Current _Batch", StringComparison.Ordinal), "go-batch-menu");
        Check(xaml.Contains("Name=\"RunBatchButton\"", StringComparison.Ordinal), "go-batch-toolbar");
        var batch = MethodBody(window, "void OnRunCurrentBatch");
        Check(batch.Contains("GoBatchExecution.ExecuteOneAsync", StringComparison.Ordinal)
              || batch.Contains("GoBatchExecution.Plan", StringComparison.Ordinal), "go-batch-uses-one-batch");
        Check(batch.Contains("ResolveCaret", StringComparison.Ordinal), "go-batch-caret-or-selection");

        Check(xaml.Contains("Refresh Selected", StringComparison.Ordinal), "subtree-refresh-context-or-button");
        Check(shell.Contains("Refresh _Selected", StringComparison.Ordinal), "subtree-refresh-menu");
        var refreshOne = MethodBody(window, "void OnRefreshSubtree");
        Check(refreshOne.Contains("InvalidateSubtree", StringComparison.Ordinal)
              && !refreshOne.Contains("LoadDatabasesAsync", StringComparison.Ordinal), "subtree-refresh-does-not-reload-tree");
        var refreshAll = MethodBody(window, "void OnRefreshTree");
        Check(refreshAll.Contains("LoadDatabasesAsync", StringComparison.Ordinal), "subtree-refresh-full-refresh-remains");
        Check(explorer.Contains("ExplorerSubtree.Invalidate", StringComparison.Ordinal), "subtree-refresh-controller-uses-core");
        Pass("query-context-wiring");
    }

    static SubtreeNode Branch(string name, params string[] children)
    {
        var node = new SubtreeNode(name);
        foreach (var child in children)
            node.Children.Add(new SubtreeNode(child));
        return node;
    }

    static QueryResult Result(string sql) => new()
    {
        ExecutedSql = sql,
        Server = "s",
        Database = "d",
        Completion = QueryCompletionKind.Succeeded
    };

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SQLoryn.slnx")))
                return d.FullName;
        throw new Exception("FAIL repo root (SQLoryn.slnx) not found from test base directory");
    }

    static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            throw new Exception($"FAIL missing {signature}");
        var brace = source.IndexOf('{', start);
        if (brace < 0)
            throw new Exception($"FAIL {signature} has no body");
        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[start..(i + 1)];
            }
        }
        throw new Exception($"FAIL {signature} body did not close");
    }

    sealed class FakeDatabaseSession : IDatabaseSession
    {
        public FakeDatabaseSession(string login, string? database, IReadOnlyList<string> catalog, bool connected = true)
        {
            Login = login;
            Database = database;
            Catalog = catalog;
            IsConnected = connected;
        }

        public string Login { get; }
        public string? Database { get; private set; }
        public bool IsConnected { get; }
        public IReadOnlyList<string> Catalog { get; }
        public List<string> Changes { get; } = [];
        public int ListCalls { get; private set; }
        public Exception? ChangeError { get; init; }

        public Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(Catalog);
        }

        public Task ChangeDatabaseAsync(string database, CancellationToken cancellationToken = default)
        {
            if (ChangeError is not null)
                throw ChangeError;
            Changes.Add(database);
            Database = database;
            return Task.CompletedTask;
        }
    }

    sealed class SubtreeNode(string name)
    {
        public string Name { get; } = name;
        public bool Loaded { get; set; } = true;
        public List<SubtreeNode> Children { get; } = [];
    }
}
