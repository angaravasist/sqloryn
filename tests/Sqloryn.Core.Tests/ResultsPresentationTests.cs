using Sqloryn.Core;

/// <summary>
/// Results as text, provider rows-affected, NULL-distinct copy,
/// client elapsed time and the connect-timeout preference. No SQL Server connection.
/// </summary>
static class ResultsPresentationTests
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
        Pass("results-presentation");
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
        Check(ResultTextFormatter.CellToken(CellValue.Null) == "(null)", "results-text-null-marker");
        Check(ResultTextFormatter.CellToken(CellValue.Text("", "empty-string")) == "", "results-text-empty-token");
        Check(ResultTextFormatter.CellToken(CellValue.Text("NULL", "string")) == "NULL", "results-text-text-null-token");
        Check(ResultTextFormatter.CellToken(CellValue.Null) != ResultTextFormatter.CellToken(CellValue.Text("", "empty-string")), "results-text-null-distinct-from-empty");
        Check(ResultTextFormatter.CellToken(CellValue.Null) != ResultTextFormatter.CellToken(CellValue.Text("NULL", "string")), "results-text-null-distinct-from-word-null");

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
        Check(text.Contains("Does not re-query the server", StringComparison.Ordinal), "results-text-no-requery-note");
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var nullLine = lines.Single(l => l.StartsWith("(null)", StringComparison.Ordinal));
        var wordLine = lines.Single(l => l.StartsWith("NULL", StringComparison.Ordinal));
        Check(!nullLine.Contains("NULL", StringComparison.Ordinal), "results-text-null-row-is-not-the-word");
        Check(nullLine.Length > 8 && nullLine[8..].Trim().Length == 0, "results-text-empty-cell-is-blank");
        Check(wordLine.Length > 8 && wordLine[8..].Trim() == "xy", "results-text-columns-align");
        Check(ResultTextFormatter.Format(set, 1).Contains("first 1 of 2 retained rows", StringComparison.Ordinal), "results-text-caps-retained-rows");
        Pass("results-text-results-as-text");
    }

    static void RowsAffected()
    {
        Check(ProviderRowsAffected.FromRecordsAffected(-1) is null, "rows-affected-unreported-is-absent");
        Check(ProviderRowsAffected.FromRecordsAffected(-2) is null, "rows-affected-negative-is-absent");
        Check(ProviderRowsAffected.FromRecordsAffected(0) == 0, "rows-affected-zero-is-a-real-count");
        Check(ProviderRowsAffected.FromRecordsAffected(4) == 4, "rows-affected-positive-count");
        Check(ProviderRowsAffected.SumReported([null, null]) is null, "rows-affected-sum-does-not-invent-zero");
        Check(ProviderRowsAffected.SumReported([3, null, 2]) == 5, "rows-affected-sum-skips-unreported");
        Check(ProviderRowsAffected.SumReported([0]) == 0, "rows-affected-sum-keeps-zero");

        Check(QueryOutcomeFormatter.DescribeRowsAffected(null) == "", "rows-affected-label-omits-unknown");
        Check(QueryOutcomeFormatter.DescribeRowsAffected(0) == "0 rows affected (provider-reported)", "rows-affected-label-zero");
        Check(QueryOutcomeFormatter.DescribeRowsAffected(1) == "1 row affected (provider-reported)", "rows-affected-label-one");
        Check(QueryOutcomeFormatter.DescribeRowsAffected(3) == "3 rows affected (provider-reported)", "rows-affected-label-many");
        var absent = Result();
        Check(!QueryOutcomeFormatter.FormatMessages(absent).Contains("affected", StringComparison.Ordinal), "rows-affected-messages-omit-unknown");
        var reported = Result(rowsAffected: 3);
        Check(QueryOutcomeFormatter.FormatMessages(reported).Contains("3 rows affected (provider-reported)", StringComparison.Ordinal), "rows-affected-messages-show-reported");

        var agg = new BatchAggregate();
        agg.Add(Result(rowsAffected: 2, elapsedMs: 10), 1, 2);
        agg.Add(Result(elapsedMs: 5), 2, 2);
        var built = agg.Build("SELECT 1\nGO\nUPDATE t SET a = 1", "s", "d");
        Check(built.RowsAffected == 2, "rows-affected-aggregate-skips-unreported");
        var kept = SqlSession.WithTargetVerification(reported, true, "db");
        Check(kept.RowsAffected == 3, "rows-affected-target-verify-keeps-count");
        var unknown = SqlSession.WithTargetVerification(reported, false, null);
        Check(unknown.RowsAffected == 3, "rows-affected-target-failure-keeps-count");
        Pass("rows-affected-rows-affected");
    }

    static void CopyNullDistinct()
    {
        Check(ResultCopy.FormatCell(CellValue.Null) == "", "null-copy-null-copies-empty");
        Check(ResultCopy.FormatCell(CellValue.Text("NULL", "string")) == "NULL", "null-copy-word-null-copies-letters");
        Check(ResultCopy.FormatCell(CellValue.Text("", "empty-string")) == "\"\"", "null-copy-empty-copies-marker");
        Check(CellValue.Null.ClipboardText == ResultCopy.FormatCell(CellValue.Null), "null-copy-clipboard-matches-formatter");
        var copied = ResultCopy.FormatGrid(["Note"],
        [
            [CellValue.Null],
            [CellValue.Text("", "empty-string")],
            [CellValue.Text("NULL", "string")]
        ]);
        var lines = copied.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        Check(lines.Length == 4 && lines[0] == "Note" && lines[1] == "" && lines[2] == "\"\"" && lines[3] == "NULL", "null-copy-grid-header-and-three-way-null");
        Pass("null-copy-copy-null-distinct");
    }

    static void ClientElapsed()
    {
        Check(QueryOutcomeFormatter.DescribeClientElapsed(null) == "", "elapsed-omit-unmeasured");
        var label = QueryOutcomeFormatter.DescribeClientElapsed(42);
        Check(label.Contains("Client elapsed: 42 ms", StringComparison.Ordinal), "elapsed-shows-client-ms");
        Check(label.Contains("not server statistics", StringComparison.Ordinal), "elapsed-does-not-claim-server-stats");
        var absent = Result();
        Check(!QueryOutcomeFormatter.FormatMessages(absent).Contains("Client elapsed", StringComparison.Ordinal), "elapsed-messages-omit-unmeasured");
        var measured = Result(elapsedMs: 42);
        Check(QueryOutcomeFormatter.FormatMessages(measured).Contains(label, StringComparison.Ordinal), "elapsed-messages-show-client-elapsed");
        var agg = new BatchAggregate();
        agg.Add(Result(elapsedMs: 10), 1, 2);
        agg.Add(Result(elapsedMs: 5), 2, 2);
        Check(agg.Build("a", "s", "d").ClientElapsedMilliseconds == 15, "elapsed-aggregate-sums-client-ms");
        var skipped = new BatchAggregate();
        skipped.Add(Result(), 1, 1);
        Check(skipped.Build("a", "s", "d").ClientElapsedMilliseconds is null, "elapsed-aggregate-does-not-invent-zero");
        var kept = SqlSession.WithTargetVerification(measured, false, null);
        Check(kept.ClientElapsedMilliseconds == 42, "elapsed-target-failure-keeps-elapsed");
        Pass("elapsed-client-elapsed");
    }

    static void ConnectTimeoutSetting()
    {
        Check(AppSettings.DefaultConnectTimeoutSeconds == 15, "connect-timeout-default-constant");
        Check(AppSettings.MinConnectTimeoutSeconds == 1 && AppSettings.MaxConnectTimeoutSeconds == 120, "connect-timeout-bounds");
        Check(new AppSettings().ConnectTimeoutSeconds == 15, "connect-timeout-default-15");
        Check(new AppSettings { ConnectTimeoutSeconds = 0 }.Normalize().ConnectTimeoutSeconds == 1, "connect-timeout-clamp-low");
        Check(new AppSettings { ConnectTimeoutSeconds = 121 }.Normalize().ConnectTimeoutSeconds == 120, "connect-timeout-clamp-high");
        Check(new AppSettings { ConnectTimeoutSeconds = 30 }.Normalize().ConnectTimeoutSeconds == 30, "connect-timeout-in-range-kept");

        var dir = Directory.CreateTempSubdirectory("sqloryn-connect-timeout");
        try
        {
            var store = new AppSettingsStore(dir.FullName);
            store.Save(new AppSettings { ConnectTimeoutSeconds = 45 });
            var loaded = new AppSettingsStore(dir.FullName).Load();
            Check(loaded.ConnectTimeoutSeconds == 45, "connect-timeout-persisted");
            Check(File.ReadAllText(store.FilePath).Contains("\"connectTimeoutSeconds\": 45", StringComparison.Ordinal), "connect-timeout-json-camel");
        }
        finally
        {
            dir.Delete(true);
        }
        Pass("connect-timeout-connect-timeout-setting");
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

        Check(shell.Contains("Results as _Text", StringComparison.Ordinal) && shell.Contains("Results as _Grid", StringComparison.Ordinal), "results-text-menu-toggle");
        Check(shell.Contains("Copy _Results", StringComparison.Ordinal), "null-copy-menu-copy");
        var toggle = MethodBody(shell, "void OnResultsAsText");
        Check(toggle.Contains("RenderResult", StringComparison.Ordinal) && !toggle.Contains("ExecuteAsync", StringComparison.Ordinal), "results-text-toggle-renders-without-query");
        Check(results.Contains("ResultTextFormatter.Format", StringComparison.Ordinal), "results-text-text-uses-retained-formatter");
        Check(results.Contains(".ClipboardText", StringComparison.Ordinal), "null-copy-grid-copy-binding");
        Check(results.Contains("ResultCopy.FormatGrid", StringComparison.Ordinal) || results.Contains("ResultCopy.FormatCell", StringComparison.Ordinal), "null-copy-copy-command-uses-formatter");
        Check(results.Contains("DescribeRowsAffected", StringComparison.Ordinal), "rows-affected-summary-wires-label");
        Check(results.Contains("DescribeClientElapsed", StringComparison.Ordinal), "elapsed-summary-wires-elapsed");
        Check(session.Contains("Stopwatch", StringComparison.Ordinal) && session.Contains("ClientElapsedMilliseconds", StringComparison.Ordinal), "elapsed-session-measures-client-elapsed");
        Check(session.Contains("ProviderRowsAffected.FromRecordsAffected", StringComparison.Ordinal), "rows-affected-session-reads-provider-count");
        Check(settingsXaml.Contains("Name=\"ConnectTimeoutBox\"", StringComparison.Ordinal), "connect-timeout-settings-control");
        Check(settingsXaml.Contains("Minimum=\"1\"", StringComparison.Ordinal) && settingsXaml.Contains("Maximum=\"120\"", StringComparison.Ordinal), "connect-timeout-settings-bounds");
        Check(!settingsXaml.Contains("Command timeout", StringComparison.OrdinalIgnoreCase), "connect-timeout-leaves-command-timeout-alone");
        Check(settingsCs.Contains("ConnectTimeoutSeconds", StringComparison.Ordinal), "connect-timeout-settings-persists");
        Check(connect.Contains("ConnectTimeoutSeconds", StringComparison.Ordinal) && connect.Contains("Settings.Load()", StringComparison.Ordinal), "connect-timeout-connect-applies-setting");
        Pass("results-presentation-wiring");
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
