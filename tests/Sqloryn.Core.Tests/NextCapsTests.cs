using Sqloryn.Core;

/// <summary>
/// Database context on the active tab, session ANSI settings,
/// running one GO batch and refreshing one Object Explorer subtree. No SQL Server connection.
/// </summary>
static class NextCapsTests
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
        Pass("next-caps");
    }

    static async Task DatabaseSwitch()
    {
        var active = new FakeDatabaseSession("login-a", "AppDb", ["AppDb", "OtherDb"]);
        var other = new FakeDatabaseSession("login-b", "KeepDb", ["KeepDb", "OtherDb"]);
        var outcome = await ActiveDatabaseSwitch.ApplyAsync(active, [other], "OtherDb");
        Check(outcome.Applied && outcome.Database == "OtherDb", "cap060-active-database");
        Check(active.Database == "OtherDb" && active.Changes.Count == 1 && active.Changes[0] == "OtherDb", "cap060-one-change-call");
        Check(other.Database == "KeepDb" && other.Changes.Count == 0 && other.ListCalls == 0, "cap060-other-tab-unchanged");
        Check(active.Login == "login-a" && other.Login == "login-b", "cap060-logins-untouched");
        Check(outcome.Message.Contains("not re-entered", StringComparison.Ordinal)
              && outcome.Message.Contains("Other query tabs", StringComparison.Ordinal), "cap060-message");

        var same = await ActiveDatabaseSwitch.ApplyAsync(active, [other], "otherdb");
        Check(same.Applied && active.Changes.Count == 1 && other.Database == "KeepDb", "cap060-same-database-is-noop");

        var missing = await ActiveDatabaseSwitch.ApplyAsync(active, [other], "Missing");
        Check(!missing.Applied && active.Database == "OtherDb" && other.Changes.Count == 0, "cap060-unknown-database-refused");
        Check(missing.Message.Contains("not in the databases", StringComparison.Ordinal), "cap060-unknown-explains");

        var offline = new FakeDatabaseSession("login-a", null, ["AppDb"], connected: false);
        var refused = await ActiveDatabaseSwitch.ApplyAsync(offline, [other], "AppDb");
        Check(!refused.Applied && offline.ListCalls == 0 && offline.Changes.Count == 0 && other.Database == "KeepDb", "cap060-disconnected-does-not-list");
        Check(refused.Message.Contains("Connect", StringComparison.Ordinal)
              && refused.Message.Contains("not requested", StringComparison.Ordinal), "cap060-disconnected-message");

        var failing = new FakeDatabaseSession("login-a", "AppDb", ["AppDb", "OtherDb"])
        {
            ChangeError = new InvalidOperationException("Refusing database change while a transaction is open.")
        };
        try
        {
            await ActiveDatabaseSwitch.ApplyAsync(failing, [other], "OtherDb");
            throw new Exception("FAIL cap060-transaction-should-throw");
        }
        catch (InvalidOperationException)
        {
            Check(failing.Database == "AppDb" && other.Database == "KeepDb", "cap060-failed-change-keeps-both");
        }

        var change = typeof(IDatabaseSession).GetMethod(nameof(IDatabaseSession.ChangeDatabaseAsync));
        Check(change is not null && change.GetParameters().Count(p => p.ParameterType == typeof(string)) == 1, "cap060-no-credential-parameter");
        Pass("cap060-active-database-switch");
    }

    static void AnsiSettings()
    {
        var settings = new QueryAnsiSettings(false, true, false, true);
        var sql = settings.ToSessionScript();
        Check(sql.Contains("SET ANSI_NULLS OFF;", StringComparison.Ordinal), "cap054-ansi-nulls");
        Check(sql.Contains("SET ANSI_PADDING ON;", StringComparison.Ordinal), "cap054-ansi-padding");
        Check(sql.Contains("SET QUOTED_IDENTIFIER OFF;", StringComparison.Ordinal), "cap054-quoted-identifier");
        Check(sql.Contains("SET ARITHABORT ON;", StringComparison.Ordinal), "cap054-arithabort");
        Check(sql.Contains("PRINT ", StringComparison.Ordinal), "cap054-print-for-messages");
        Check(sql.Contains("SESSIONPROPERTY(N'ANSI_NULLS')", StringComparison.Ordinal)
              && sql.Contains("SESSIONPROPERTY(N'ANSI_PADDING')", StringComparison.Ordinal)
              && sql.Contains("SESSIONPROPERTY(N'QUOTED_IDENTIFIER')", StringComparison.Ordinal)
              && sql.Contains("SESSIONPROPERTY(N'ARITHABORT')", StringComparison.Ordinal), "cap054-session-property");
        Check(!sql.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase), "cap054-script-has-no-timeout");
        Check(!sql.Contains("GO", StringComparison.OrdinalIgnoreCase), "cap054-single-batch");
        Check(QueryAnsiSettings.ScopeNote.Contains("SSMS", StringComparison.Ordinal)
              && QueryAnsiSettings.ScopeNote.Contains("command timeout", StringComparison.OrdinalIgnoreCase)
              && QueryAnsiSettings.ScopeNote.Contains("session", StringComparison.OrdinalIgnoreCase), "cap054-honest-scope");
        Pass("cap054-ansi-script");
    }

    static async Task OneBatch()
    {
        var script = "SELECT 1\nGO\nSELECT 2\nGO\nSELECT 3";
        Check(GoBatchSplitter.Choose(script, script.IndexOf("SELECT 2", StringComparison.Ordinal)).Text == "SELECT 2", "cap071-caret-in-middle");
        Check(GoBatchSplitter.Choose(script, script.IndexOf("GO", StringComparison.Ordinal)).Text == "SELECT 1", "cap071-caret-on-go");
        Check(GoBatchSplitter.Choose(script, script.Length).Text == "SELECT 3", "cap071-caret-at-end");
        var blank = "SELECT 1\n\nGO\nSELECT 2";
        Check(GoBatchSplitter.Choose(blank, blank.IndexOf("\n\n", StringComparison.Ordinal) + 1).Text == "SELECT 1", "cap071-caret-in-gap");
        Check(GoBatchSplitter.Choose("GO\nSELECT 1", 0).Text == "SELECT 1", "cap071-leading-go");
        var quoted = "SELECT 'GO'\nGO\nSELECT 2";
        Check(GoBatchSplitter.Choose(quoted, 0).Text == "SELECT 'GO'", "cap071-go-inside-string");
        Check(GoBatchSplitter.Choose(quoted, quoted.IndexOf("SELECT 2", StringComparison.Ordinal)).Text == "SELECT 2", "cap071-after-string-go");

        var calls = new List<string>();
        var ran = await GoBatchExecution.ExecuteOneAsync(
            (sql, _) =>
            {
                calls.Add(sql);
                return Task.FromResult(Result(sql));
            },
            script,
            GoBatchExecution.ResolveCaret(script.Length, script.IndexOf("SELECT 2", StringComparison.Ordinal), 8));
        Check(calls.Count == 1 && calls[0] == "SELECT 2", "cap071-executes-one-text");
        Check(ran.ExecutedSql == "SELECT 2", "cap071-result-is-chosen-batch");

        var repeated = "SELECT 1\nGO 3\nSELECT 2";
        var plan = GoBatchExecution.Plan(repeated, 0);
        Check(plan.Sql == "SELECT 1" && plan.RepeatCount == 3 && plan.Count == 2, "cap071-repeat-is-recorded");
        calls.Clear();
        await GoBatchExecution.ExecuteOneAsync((sql, _) =>
        {
            calls.Add(sql);
            return Task.FromResult(Result(sql));
        }, repeated, 0);
        Check(calls.Count == 1 && calls[0] == "SELECT 1", "cap071-repeat-runs-once");
        Check(plan.Note.Contains("once", StringComparison.Ordinal)
              && plan.Note.Contains("not repeated", StringComparison.Ordinal)
              && plan.Note.Contains("Other batches", StringComparison.Ordinal), "cap071-note");

        calls.Clear();
        try
        {
            await GoBatchExecution.ExecuteOneAsync((sql, _) =>
            {
                calls.Add(sql);
                return Task.FromResult(Result(sql));
            }, "SELECT 1\nGO 0\nSELECT 2", 0);
            throw new Exception("FAIL cap071-malformed-should-reject");
        }
        catch (SqlScriptRejectedException)
        {
            Check(calls.Count == 0, "cap071-malformed-executes-nothing");
        }

        var only = GoBatchExecution.Plan("SELECT 1", 3);
        Check(only.Sql == "SELECT 1" && only.Count == 1, "cap071-no-go-is-one-batch");
        try
        {
            GoBatchExecution.Plan("GO\n  \nGO", 0);
            throw new Exception("FAIL cap071-empty-should-refuse");
        }
        catch (InvalidOperationException)
        {
            Pass("cap071-empty-refused");
        }

        Check(GoBatchExecution.ResolveCaret(10, 4, 0) == 10, "cap071-caret-without-selection");
        Check(GoBatchExecution.ResolveCaret(10, 4, 3) == 4, "cap071-selection-uses-its-start");
        Pass("cap071-one-batch");
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

        Check(!tables.Loaded && tables.Children.Count == 0, "cap040-selected-cleared");
        Check(views.Loaded && views.Children.Count == 1 && views.Children[0].Name == "V1", "cap040-sibling-kept");
        Check(schema.Loaded && schema.Children.Count == 2 && ReferenceEquals(schema.Children[0], tables), "cap040-parent-kept");
        Check(otherDb.Loaded && otherDb.Children.Count == 1 && otherDb.Children[0].Name == "keep", "cap040-other-database-kept");
        Check(root.Children.Count == 2, "cap040-roots-kept");
        Pass("cap040-subtree");
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

        Check(xaml.Contains("Name=\"DatabaseBox\"", StringComparison.Ordinal), "cap060-database-combo");
        Check(shell.Contains("Change _Database", StringComparison.Ordinal), "cap060-menu");
        Check(window.Contains("ActiveDatabaseSwitch.ApplyAsync", StringComparison.Ordinal), "cap060-uses-switch");
        var databaseHandler = MethodBody(window, "void OnDatabaseChanged");
        Check(databaseHandler.Contains("_active", StringComparison.Ordinal)
              && databaseHandler.Contains("ActiveDatabaseSwitch.ApplyAsync", StringComparison.Ordinal)
              && !databaseHandler.Contains("ConnectAsync", StringComparison.Ordinal), "cap060-active-tab-without-login");

        Check(shell.Contains("Session _ANSI Settings", StringComparison.Ordinal), "cap054-menu");
        Check(dialogXaml.Contains("ANSI_NULLS", StringComparison.Ordinal)
              && dialogXaml.Contains("ANSI_PADDING", StringComparison.Ordinal)
              && dialogXaml.Contains("QUOTED_IDENTIFIER", StringComparison.Ordinal)
              && dialogXaml.Contains("ARITHABORT", StringComparison.Ordinal), "cap054-dialog-options");
        Check(dialog.Contains("QueryAnsiSettings", StringComparison.Ordinal)
              && dialog.Contains("ToSessionScript", StringComparison.Ordinal)
              && dialog.Contains("ScopeNote", StringComparison.Ordinal), "cap054-dialog-previews-core-script");
        Check(!dialogXaml.Contains("timeout", StringComparison.OrdinalIgnoreCase), "cap054-dialog-has-no-timeout");
        var ansi = MethodBody(window, "void OnSessionAnsi");
        Check(ansi.Contains("QueryOptionsWindow", StringComparison.Ordinal)
              && ansi.Contains("_active", StringComparison.Ordinal)
              && ansi.Contains("ExecuteOnTabAsync", StringComparison.Ordinal), "cap054-apply-on-active-tab");

        Check(shell.Contains("Run Current _Batch", StringComparison.Ordinal), "cap071-menu");
        Check(xaml.Contains("Name=\"RunBatchButton\"", StringComparison.Ordinal), "cap071-toolbar");
        var batch = MethodBody(window, "void OnRunCurrentBatch");
        Check(batch.Contains("GoBatchExecution.ExecuteOneAsync", StringComparison.Ordinal)
              || batch.Contains("GoBatchExecution.Plan", StringComparison.Ordinal), "cap071-uses-one-batch");
        Check(batch.Contains("ResolveCaret", StringComparison.Ordinal), "cap071-caret-or-selection");

        Check(xaml.Contains("Refresh Selected", StringComparison.Ordinal), "cap040-context-or-button");
        Check(shell.Contains("Refresh _Selected", StringComparison.Ordinal), "cap040-menu");
        var refreshOne = MethodBody(window, "void OnRefreshSubtree");
        Check(refreshOne.Contains("InvalidateSubtree", StringComparison.Ordinal)
              && !refreshOne.Contains("LoadDatabasesAsync", StringComparison.Ordinal), "cap040-does-not-reload-tree");
        var refreshAll = MethodBody(window, "void OnRefreshTree");
        Check(refreshAll.Contains("LoadDatabasesAsync", StringComparison.Ordinal), "cap040-full-refresh-remains");
        Check(explorer.Contains("ExplorerSubtree.Invalidate", StringComparison.Ordinal), "cap040-controller-uses-core");
        Pass("next-caps-wiring");
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
