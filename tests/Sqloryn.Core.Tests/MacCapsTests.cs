using Sqloryn.Core;

/// <summary>
/// Results as text, provider rows-affected, NULL-distinct copy,
/// client elapsed time and the connect-timeout preference. No SQL Server connection.
/// </summary>
static class MacCapsTests
{
    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"FAIL {name}");
    }

    static void Pass(string name) => Console.WriteLine($"PASS {name}");

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SQLoryn.slnx")))
                return d.FullName;
        throw new Exception("FAIL repo root (SQLoryn.slnx) not found from test base directory");
    }

    public static void Run()
    {
        ResultsAsText();
        RowsAffected();
        CopyNullDistinct();
        ClientElapsed();
        ConnectTimeoutSetting();
        Wiring();
        Pass("mac-caps");
    }

    static QueryResult Result(int? rowsAffected = null, long? elapsedMs = null) => new()
    {
        ExecutedSql = "SELECT 1",
        Server = "s",
        Database = "d",
        Completion = QueryCompletionKind.Succeeded,
        RowsAffected = rowsAffected,
        ClientElapsedMilliseconds = elapsedMs
    };

    static void ResultsAsText()
    {
        Check(ResultTextFormatter.CellToken(CellValue.Null) == "(null)", "cap084-null-marker");
        Check(ResultTextFormatter.CellToken(CellValue.Text("", "empty-string")) == "", "cap084-empty-token");
        Check(ResultTextFormatter.CellToken(CellValue.Text("NULL", "string")) == "NULL", "cap084-text-null-token");
        Check(ResultTextFormatter.CellToken(CellValue.Null) != ResultTextFormatter.CellToken(CellValue.Text("", "empty-string")), "cap084-null-distinct-from-empty");
        Check(ResultTextFormatter.CellToken(CellValue.Null) != ResultTextFormatter.CellToken(CellValue.Text("NULL", "string")), "cap084-null-distinct-from-word-null");

        var set = new ResultSet
        {
            Columns = ["A", "B"],
            Rows =
            [
                [CellValue.Null, CellValue.Text("", "empty-string")],
                [CellValue.Text("NULL", "string"), CellValue.Text("xy", "string")]
            ]
        };
        var text = ResultTextFormatter.Format(set, 10);
        Check(text.Contains("Does not re-query the server", StringComparison.Ordinal), "cap084-no-requery-note");
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var nullLine = lines.Single(l => l.StartsWith("(null)", StringComparison.Ordinal));
        var wordLine = lines.Single(l => l.StartsWith("NULL", StringComparison.Ordinal));
        Check(!nullLine.Contains("NULL", StringComparison.Ordinal), "cap084-null-row-is-not-the-word");
        Check(nullLine.Length > 8 && nullLine[8..].Trim().Length == 0, "cap084-empty-cell-is-blank");
        Check(wordLine.Length > 8 && wordLine[8..].Trim() == "xy", "cap084-columns-align");
        Check(ResultTextFormatter.Format(set, 1).Contains("first 1 of 2 retained rows", StringComparison.Ordinal), "cap084-caps-retained-rows");
        Pass("cap084-results-as-text");
    }

    static void RowsAffected()
    {
        Check(ProviderRowsAffected.FromRecordsAffected(-1) is null, "cap075-unreported-is-absent");
        Check(ProviderRowsAffected.FromRecordsAffected(-2) is null, "cap075-negative-is-absent");
        Check(ProviderRowsAffected.FromRecordsAffected(0) == 0, "cap075-zero-is-a-real-count");
        Check(ProviderRowsAffected.FromRecordsAffected(4) == 4, "cap075-positive-count");
        Check(ProviderRowsAffected.SumReported([null, null]) is null, "cap075-sum-does-not-invent-zero");
        Check(ProviderRowsAffected.SumReported([3, null, 2]) == 5, "cap075-sum-skips-unreported");
        Check(ProviderRowsAffected.SumReported([0]) == 0, "cap075-sum-keeps-zero");

        Check(QueryOutcomeFormatter.DescribeRowsAffected(null) == "", "cap075-label-omits-unknown");
        Check(QueryOutcomeFormatter.DescribeRowsAffected(0) == "0 rows affected (provider-reported)", "cap075-label-zero");
        Check(QueryOutcomeFormatter.DescribeRowsAffected(1) == "1 row affected (provider-reported)", "cap075-label-one");
        Check(QueryOutcomeFormatter.DescribeRowsAffected(3) == "3 rows affected (provider-reported)", "cap075-label-many");
        var absent = Result();
        Check(!QueryOutcomeFormatter.FormatMessages(absent).Contains("affected", StringComparison.Ordinal), "cap075-messages-omit-unknown");
        var reported = Result(rowsAffected: 3);
        Check(QueryOutcomeFormatter.FormatMessages(reported).Contains("3 rows affected (provider-reported)", StringComparison.Ordinal), "cap075-messages-show-reported");

        var agg = new BatchAggregate();
        agg.Add(Result(rowsAffected: 2, elapsedMs: 10), 1, 2);
        agg.Add(Result(elapsedMs: 5), 2, 2);
        var built = agg.Build("SELECT 1\nGO\nUPDATE t SET a = 1", "s", "d");
        Check(built.RowsAffected == 2, "cap075-aggregate-skips-unreported");
        var kept = SqlSession.WithTargetVerification(reported, true, "db");
        Check(kept.RowsAffected == 3, "cap075-target-verify-keeps-count");
        var unknown = SqlSession.WithTargetVerification(reported, false, null);
        Check(unknown.RowsAffected == 3, "cap075-target-failure-keeps-count");
        Pass("cap075-rows-affected");
    }

    static void CopyNullDistinct()
    {
        Check(ResultCopy.FormatCell(CellValue.Null) == "", "cap087-null-copies-empty");
        Check(ResultCopy.FormatCell(CellValue.Text("NULL", "string")) == "NULL", "cap087-word-null-copies-letters");
        Check(ResultCopy.FormatCell(CellValue.Text("", "empty-string")) == "\"\"", "cap087-empty-copies-marker");
        Check(CellValue.Null.ClipboardText == ResultCopy.FormatCell(CellValue.Null), "cap087-clipboard-matches-formatter");
        var copied = ResultCopy.FormatGrid(["Note"],
        [
            [CellValue.Null],
            [CellValue.Text("", "empty-string")],
            [CellValue.Text("NULL", "string")]
        ]);
        var lines = copied.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        Check(lines.Length == 4 && lines[0] == "Note" && lines[1] == "" && lines[2] == "\"\"" && lines[3] == "NULL", "cap087-grid-header-and-three-way-null");
        Pass("cap087-copy-null-distinct");
    }

    static void ClientElapsed()
    {
        Check(QueryOutcomeFormatter.DescribeClientElapsed(null) == "", "cap099-omit-unmeasured");
        var label = QueryOutcomeFormatter.DescribeClientElapsed(42);
        Check(label.Contains("Client elapsed: 42 ms", StringComparison.Ordinal), "cap099-shows-client-ms");
        Check(label.Contains("not server statistics", StringComparison.Ordinal), "cap099-does-not-claim-server-stats");
        var absent = Result();
        Check(!QueryOutcomeFormatter.FormatMessages(absent).Contains("Client elapsed", StringComparison.Ordinal), "cap099-messages-omit-unmeasured");
        var measured = Result(elapsedMs: 42);
        Check(QueryOutcomeFormatter.FormatMessages(measured).Contains(label, StringComparison.Ordinal), "cap099-messages-show-client-elapsed");
        var agg = new BatchAggregate();
        agg.Add(Result(elapsedMs: 10), 1, 2);
        agg.Add(Result(elapsedMs: 5), 2, 2);
        Check(agg.Build("a", "s", "d").ClientElapsedMilliseconds == 15, "cap099-aggregate-sums-client-ms");
        var skipped = new BatchAggregate();
        skipped.Add(Result(), 1, 1);
        Check(skipped.Build("a", "s", "d").ClientElapsedMilliseconds is null, "cap099-aggregate-does-not-invent-zero");
        var kept = SqlSession.WithTargetVerification(measured, false, null);
        Check(kept.ClientElapsedMilliseconds == 42, "cap099-target-failure-keeps-elapsed");
        Pass("cap099-client-elapsed");
    }

    static void ConnectTimeoutSetting()
    {
        Check(AppSettings.DefaultConnectTimeoutSeconds == 15, "cap014-default-constant");
        Check(AppSettings.MinConnectTimeoutSeconds == 1 && AppSettings.MaxConnectTimeoutSeconds == 120, "cap014-bounds");
        Check(new AppSettings().ConnectTimeoutSeconds == 15, "cap014-default-15");
        Check(new AppSettings { ConnectTimeoutSeconds = 0 }.Normalize().ConnectTimeoutSeconds == 1, "cap014-clamp-low");
        Check(new AppSettings { ConnectTimeoutSeconds = 121 }.Normalize().ConnectTimeoutSeconds == 120, "cap014-clamp-high");
        Check(new AppSettings { ConnectTimeoutSeconds = 30 }.Normalize().ConnectTimeoutSeconds == 30, "cap014-in-range-kept");

        var dir = Directory.CreateTempSubdirectory("sqloryn-cap014");
        try
        {
            var store = new AppSettingsStore(dir.FullName);
            store.Save(new AppSettings { ConnectTimeoutSeconds = 45 });
            var loaded = new AppSettingsStore(dir.FullName).Load();
            Check(loaded.ConnectTimeoutSeconds == 45, "cap014-persisted");
            Check(File.ReadAllText(store.FilePath).Contains("\"connectTimeoutSeconds\": 45", StringComparison.Ordinal), "cap014-json-camel");
        }
        finally
        {
            dir.Delete(true);
        }
        Pass("cap014-connect-timeout-setting");
    }

    static void Wiring()
    {
        var app = Path.Combine(FindRepoRoot(), "src", "Sqloryn.App");
        var shell = File.ReadAllText(Path.Combine(app, "MainWindow.Shell.cs"));
        var results = File.ReadAllText(Path.Combine(app, "MainWindow.Results.cs"));
        var session = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Sqloryn.Core", "SqlSession.cs"));
        var settingsXaml = File.ReadAllText(Path.Combine(app, "SettingsWindow.axaml"));
        var settingsCs = File.ReadAllText(Path.Combine(app, "SettingsWindow.axaml.cs"));
        var connect = File.ReadAllText(Path.Combine(app, "ConnectWindow.axaml.cs"));

        Check(shell.Contains("Results as _Text", StringComparison.Ordinal) && shell.Contains("Results as _Grid", StringComparison.Ordinal), "cap084-menu-toggle");
        Check(shell.Contains("Copy _Results", StringComparison.Ordinal), "cap087-menu-copy");
        var toggle = MethodBody(shell, "void OnResultsAsText");
        Check(toggle.Contains("RenderResult", StringComparison.Ordinal) && !toggle.Contains("ExecuteAsync", StringComparison.Ordinal), "cap084-toggle-renders-without-query");
        Check(results.Contains("ResultTextFormatter.Format", StringComparison.Ordinal), "cap084-text-uses-retained-formatter");
        Check(results.Contains(".ClipboardText", StringComparison.Ordinal), "cap087-grid-copy-binding");
        Check(results.Contains("ResultCopy.FormatGrid", StringComparison.Ordinal) || results.Contains("ResultCopy.FormatCell", StringComparison.Ordinal), "cap087-copy-command-uses-formatter");
        Check(results.Contains("DescribeRowsAffected", StringComparison.Ordinal), "cap075-summary-wires-label");
        Check(results.Contains("DescribeClientElapsed", StringComparison.Ordinal), "cap099-summary-wires-elapsed");
        Check(session.Contains("Stopwatch", StringComparison.Ordinal) && session.Contains("ClientElapsedMilliseconds", StringComparison.Ordinal), "cap099-session-measures-client-elapsed");
        Check(session.Contains("ProviderRowsAffected.FromRecordsAffected", StringComparison.Ordinal), "cap075-session-reads-provider-count");
        Check(settingsXaml.Contains("Name=\"ConnectTimeoutBox\"", StringComparison.Ordinal), "cap014-settings-control");
        Check(settingsXaml.Contains("Minimum=\"1\"", StringComparison.Ordinal) && settingsXaml.Contains("Maximum=\"120\"", StringComparison.Ordinal), "cap014-settings-bounds");
        Check(!settingsXaml.Contains("Command timeout", StringComparison.OrdinalIgnoreCase), "cap014-leaves-command-timeout-alone");
        Check(settingsCs.Contains("ConnectTimeoutSeconds", StringComparison.Ordinal), "cap014-settings-persists");
        Check(connect.Contains("ConnectTimeoutSeconds", StringComparison.Ordinal) && connect.Contains("Settings.Load()", StringComparison.Ordinal), "cap014-connect-applies-setting");
        Pass("mac-caps-wiring");
    }

    static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            throw new Exception($"FAIL missing {signature}");
        var next = source.IndexOf("\n    void ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }
}
