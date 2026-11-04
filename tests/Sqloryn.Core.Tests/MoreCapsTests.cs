using System.Text.Json;
using Sqloryn.Core;

/// <summary>
/// CAP-081 SQL warning labels, CAP-046 unsaved recovery, CAP-064 execution history,
/// CAP-091 varbinary hex display. No SQL Server connection.
/// </summary>
static class MoreCapsTests
{
    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"FAIL {name}");
    }

    static void Pass(string name) => Console.WriteLine($"PASS {name}");

    public static void Run()
    {
        SqlWarnings();
        UnsavedRecovery();
        RecallHistory();
        BinaryRendering();
        Wiring();
        Pass("more-caps");
    }

    static QueryResult Result(QueryCompletionKind completion, long? elapsed, params QueryMessage[] messages) => new()
    {
        ExecutedSql = "PRINT 'x'",
        Server = "s",
        Database = "d",
        Completion = completion,
        ClientElapsedMilliseconds = elapsed,
        Messages = messages
    };

    static void SqlWarnings()
    {
        var info = QueryOutcomeFormatter.DescribeNotice(new QueryMessage(0, 0, "hello from print"));
        Check(info.StartsWith("Info ", StringComparison.Ordinal)
              && info.Contains("severity 0", StringComparison.Ordinal)
              && info.Contains("message 0", StringComparison.Ordinal)
              && info.Contains("hello from print", StringComparison.Ordinal)
              && !info.Contains("Error", StringComparison.Ordinal)
              && !info.Contains("Warning", StringComparison.Ordinal), "cap081-print-is-info");

        var warning = QueryOutcomeFormatter.DescribeNotice(new QueryMessage(9, 50000, "low severity"));
        Check(warning.StartsWith("Warning ", StringComparison.Ordinal)
              && warning.Contains("severity 9", StringComparison.Ordinal)
              && !warning.Contains("Error", StringComparison.Ordinal), "cap081-low-severity-is-warning");

        var status = QueryOutcomeFormatter.DescribeNotice(new QueryMessage(10, 0, "status line"));
        Check(status.StartsWith("Info ", StringComparison.Ordinal)
              && !status.StartsWith("Error", StringComparison.Ordinal)
              && !status.StartsWith("Warning", StringComparison.Ordinal), "cap081-severity-10-is-info");

        var error = QueryOutcomeFormatter.DescribeNotice(new QueryMessage(16, 8134, "divide by zero"));
        Check(error.StartsWith("Error ", StringComparison.Ordinal)
              && error.Contains("severity 16", StringComparison.Ordinal)
              && !error.Contains("Warning", StringComparison.Ordinal)
              && !error.StartsWith("Info", StringComparison.Ordinal), "cap081-high-severity-is-error");

        var formatted = QueryOutcomeFormatter.FormatMessages(Result(
            QueryCompletionKind.Succeeded,
            4,
            new QueryMessage(0, 0, "printed"),
            new QueryMessage(16, 208, "invalid object")));
        Check(formatted.Contains("Info (severity 0, message 0): printed", StringComparison.Ordinal), "cap081-messages-label-info");
        Check(formatted.Contains("Error (severity 16, message 208): invalid object", StringComparison.Ordinal), "cap081-messages-label-error");
        Check(!formatted.Contains("(class ", StringComparison.Ordinal), "cap081-messages-do-not-hide-kind-behind-class");

        var summary = QueryOutcomeFormatter.DescribeNoticeSummary(
        [
            new QueryMessage(0, 0, "printed"),
            new QueryMessage(8, 50000, "careful"),
            new QueryMessage(16, 208, "invalid object")
        ]);
        Check(summary.Contains("1 warning", StringComparison.Ordinal)
              && summary.Contains("1 info", StringComparison.Ordinal)
              && summary.Contains("1 error", StringComparison.Ordinal), "cap081-summary-counts-each-kind");
        Check(QueryOutcomeFormatter.DescribeNoticeSummary([]) == "", "cap081-summary-empty-when-no-notices");
        Pass("cap081-sql-warnings");
    }

    static void UnsavedRecovery()
    {
        var now = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        var dirty = DocumentRecovery.Capture("tab-1", "Untitled.sql", null, "SELECT 1;\n", now);
        Check(dirty.Id == "tab-1" && dirty.Text == "SELECT 1;\n" && !dirty.TextWasRedacted && !dirty.TextWasTruncated, "cap046-captures-sql");
        Check(dirty.FilePath is null, "cap046-untitled-has-no-path");

        var secret = DocumentRecovery.Capture("tab-2", "login.sql", "/tmp/login.sql", "password = 'hunter2';\nSELECT 1;", now);
        Check(!secret.Text.Contains("hunter2", StringComparison.Ordinal), "cap046-redacts-password");
        Check(secret.TextWasRedacted, "cap046-redaction-is-visible");
        Check(secret.FilePath == "/tmp/login.sql", "cap046-keeps-script-path");

        var huge = new string('a', DocumentRecovery.MaxTextChars + 25) + "TAIL";
        var shortened = DocumentRecovery.Capture("tab-3", "big.sql", null, huge, now);
        Check(shortened.TextWasTruncated && shortened.Text.Length == DocumentRecovery.MaxTextChars && !shortened.Text.Contains("TAIL", StringComparison.Ordinal), "cap046-bounds-text");

        var stored = new[] { dirty, secret };
        var updated = dirty with { Text = "SELECT 2;\n" };
        var replaced = DocumentRecovery.Merge(stored, [updated], [], []);
        Check(replaced.Count == 2 && replaced.Single(d => d.Id == "tab-1").Text == "SELECT 2;\n" && replaced.Any(d => d.Id == "tab-2"), "cap046-merge-updates-open-dirty");

        var kept = DocumentRecovery.Merge(stored, [], ["other-tab"], []);
        Check(kept.Count == 2, "cap046-unrelated-clean-tab-does-not-drop-copies");

        var saved = DocumentRecovery.Merge(stored, [], ["tab-1"], []);
        Check(saved.Count == 1 && saved[0].Id == "tab-2", "cap046-saving-a-tab-drops-only-that-copy");

        var adopted = DocumentRecovery.Merge(stored, [secret with { Id = "new-tab" }], [], ["tab-1", "tab-2"]);
        Check(adopted.Count == 1 && adopted[0].Id == "new-tab", "cap046-restore-replaces-adopted-copies");

        var offer = DocumentRecovery.OfferSummary([secret, shortened]);
        Check(offer.Contains("Restore", StringComparison.Ordinal)
              && offer.Contains("Discard", StringComparison.Ordinal)
              && offer.Contains("keeps", StringComparison.Ordinal), "cap046-offer-names-restore-discard-keep");
        Check(offer.Contains("Credential-like", StringComparison.Ordinal), "cap046-offer-says-redacted");
        Check(offer.Contains("shortened", StringComparison.Ordinal), "cap046-offer-says-truncated");
        Check(!offer.Contains("deleted", StringComparison.OrdinalIgnoreCase) || offer.Contains("Discard", StringComparison.Ordinal), "cap046-offer-does-not-imply-already-deleted");

        var dir = Directory.CreateTempSubdirectory("sqloryn-cap046");
        try
        {
            var store = new DocumentRecoveryStore(dir.FullName);
            Check(store.FilePath.StartsWith(dir.FullName, StringComparison.Ordinal), "cap046-file-under-chosen-root");
            Check(store.Read().Count == 0, "cap046-missing-file-is-empty");
            store.Write([dirty, secret]);
            var json = File.ReadAllText(store.FilePath);
            Check(!json.Contains("hunter2", StringComparison.Ordinal), "cap046-file-has-no-password");
            Check(!json.Contains("\"server\"", StringComparison.OrdinalIgnoreCase)
                  && !json.Contains("connectionString", StringComparison.OrdinalIgnoreCase), "cap046-file-has-no-connection");
            var read = new DocumentRecoveryStore(dir.FullName).Read();
            Check(read.Count == 2 && read[0].Id == "tab-1" && read[0].Text == "SELECT 1;\n" && read[1].TextWasRedacted, "cap046-round-trip");
            store.Write([]);
            Check(!File.Exists(store.FilePath) && store.Read().Count == 0, "cap046-empty-write-clears");
            store.Write([dirty]);
            store.Clear();
            Check(!File.Exists(store.FilePath) && new DocumentRecoveryStore(dir.FullName).Read().Count == 0, "cap046-clear-removes-copies");
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, "{not-json");
            Check(store.Read().Count == 0 && File.Exists(store.FilePath), "cap046-unreadable-copy-is-not-deleted");
        }
        finally
        {
            dir.Delete(true);
        }
        Pass("cap046-document-recovery");
    }

    static void RecallHistory()
    {
        const string sentinel = "ROW_SENTINEL_9f3a";
        var when = new DateTimeOffset(2026, 11, 4, 10, 5, 0, TimeSpan.Zero);
        var failed = new QueryResult
        {
            ExecutedSql = "SELECT 1; password = 'hunter2'; -- SQL_MARKER",
            Server = "secret-host",
            Database = "secret-db",
            Completion = QueryCompletionKind.Failed,
            ClientElapsedMilliseconds = 15,
            ErrorSummary = sentinel,
            Messages = [new QueryMessage(16, 208, sentinel)],
            ResultSets =
            [
                new ResultSet
                {
                    Columns = ["c"],
                    Rows = [[CellValue.Text(sentinel, "string")]]
                }
            ]
        };
        var entry = ExecutionHistory.FromResult(failed, when);
        Check(entry.Outcome == "Failed", "cap064-failed-outcome");
        Check(entry.ElapsedMilliseconds == 15, "cap064-keeps-elapsed");
        Check(entry.RecordedUtc == when, "cap064-keeps-time");
        Check(!entry.Sql.Contains("hunter2", StringComparison.Ordinal), "cap064-redacts-password");
        Check(entry.Sql.Contains("SQL_MARKER", StringComparison.Ordinal), "cap064-keeps-batch-text");
        Check(!entry.Sql.Contains(sentinel, StringComparison.Ordinal), "cap064-sql-omits-result-text");
        var json = JsonSerializer.Serialize(entry, AtomicFile.JsonOptions);
        Check(!json.Contains(sentinel, StringComparison.Ordinal), "cap064-entry-has-no-result-or-message-text");
        Check(!json.Contains("secret-host", StringComparison.Ordinal) && !json.Contains("secret-db", StringComparison.Ordinal), "cap064-entry-has-no-server");
        Check(!json.Contains("resultSets", StringComparison.OrdinalIgnoreCase) && !json.Contains("messages", StringComparison.OrdinalIgnoreCase), "cap064-entry-has-no-result-fields");

        var unmeasured = ExecutionHistory.FromBatch("SELECT 2", QueryCompletionKind.Succeeded, null, when);
        Check(unmeasured.Outcome == "Succeeded" && unmeasured.ElapsedMilliseconds is null, "cap064-unmeasured-elapsed-stays-absent");

        var longSql = new string('Q', ExecutionHistory.MaxSqlChars) + "TAIL_SENTINEL";
        var shortened = ExecutionHistory.FromBatch(longSql, QueryCompletionKind.Succeeded, 1, when);
        Check(!shortened.Sql.Contains("TAIL_SENTINEL", StringComparison.Ordinal)
              && shortened.Sql.Contains("truncated", StringComparison.Ordinal), "cap064-bounds-sql");

        IReadOnlyList<ExecutionHistoryEntry> list = [];
        for (var i = 0; i < ExecutionHistory.MaxEntries + 1; i++)
            list = ExecutionHistory.Remember(list, ExecutionHistory.FromBatch("batch-" + i.ToString(), QueryCompletionKind.Succeeded, i, when));
        Check(list.Count == ExecutionHistory.MaxEntries, "cap064-bounded");
        Check(list[0].Sql == "batch-" + ExecutionHistory.MaxEntries, "cap064-newest-first");
        Check(list.All(e => e.Sql != "batch-0"), "cap064-drops-oldest");

        var recalled = ExecutionHistory.ScriptForEditor(entry);
        Check(recalled == entry.Sql && !recalled.Contains("hunter2", StringComparison.Ordinal), "cap064-recall-is-stored-sql");

        var dir = Directory.CreateTempSubdirectory("sqloryn-cap064");
        try
        {
            var store = new ExecutionHistoryStore(dir.FullName);
            Check(store.Read().Count == 0, "cap064-missing-file-is-empty");
            store.Append(entry);
            store.Append(unmeasured);
            var read = new ExecutionHistoryStore(dir.FullName).Read();
            Check(read.Count == 2 && read[0].Sql == unmeasured.Sql && read[1].Outcome == "Failed", "cap064-round-trip-newest-first");
            var file = File.ReadAllText(store.FilePath);
            Check(!file.Contains("hunter2", StringComparison.Ordinal) && !file.Contains(sentinel, StringComparison.Ordinal), "cap064-file-has-no-secrets-or-rows");
            store.Clear();
            Check(!File.Exists(store.FilePath) && store.Read().Count == 0, "cap064-clear");
        }
        finally
        {
            dir.Delete(true);
        }
        Pass("cap064-execution-history");
    }

    static void BinaryRendering()
    {
        var bytes = new byte[] { 0x48, 0x69 };
        var cell = ResultMaterializer.FormatSqlTypedCell(new FakeSqlTypedCellSource(["varbinary(20)"], [bytes]), 0);
        Check(cell.Kind == "binary" && cell.Display == "0x4869", "cap091-varbinary-is-hex");
        Check(cell.Display != "Hi" && cell.ClipboardText == "0x4869", "cap091-not-decoded-as-text");

        var empty = ResultMaterializer.FormatSqlTypedCell(new FakeSqlTypedCellSource(["varbinary"], [Array.Empty<byte>()]), 0);
        Check(empty.Display == "0x" && empty.Kind == "binary", "cap091-empty-is-0x-not-blank-text");
        Check(ResultMaterializer.FormatSqlTypedCell(new FakeSqlTypedCellSource(["varbinary"], [null]), 0).IsNull, "cap091-null-stays-null");

        var binaryType = ResultMaterializer.FormatSqlTypedCell(new FakeSqlTypedCellSource(["binary(2)"], [bytes]), 0);
        Check(binaryType.Display == "0x4869" && binaryType.Kind == "binary", "cap091-binary-is-hex");

        var pretended = new ResultSet
        {
            Columns = ["blob"],
            SqlTypes = ["varbinary"],
            Rows = [[CellValue.Text("Hi", "string")]]
        };
        var refused = ResultTextFormatter.Format(pretended, 5);
        Check(!refused.Contains("Hi", StringComparison.Ordinal), "cap091-text-view-does-not-show-decoded-bytes");
        Check(refused.Contains("not shown as text", StringComparison.Ordinal), "cap091-text-view-refuses");

        var shown = ResultTextFormatter.Format(new ResultSet
        {
            Columns = ["blob"],
            SqlTypes = ["varbinary"],
            Rows = [[cell]]
        }, 5);
        Check(shown.Contains("0x4869", StringComparison.Ordinal), "cap091-text-view-shows-hex");
        Check(ResultTextFormatter.CellToken(CellValue.Null, "varbinary") == "(null)", "cap091-text-null-marker");
        Check(ResultTextFormatter.CellToken(CellValue.Text("", "empty-string"), "varchar") == "", "cap091-text-column-unchanged");
        Check(ResultTextFormatter.CellToken(CellValue.Text("Hi", "string"), "nvarchar") == "Hi", "cap091-nvarchar-stays-text");
        Pass("cap091-binary-rendering");
    }

    static void Wiring()
    {
        var root = FindRepoRoot();
        var app = Path.Combine(root, "src", "Sqloryn.App");
        var shell = File.ReadAllText(Path.Combine(app, "MainWindow.Shell.cs"));
        var results = File.ReadAllText(Path.Combine(app, "MainWindow.Results.cs"));
        var window = File.ReadAllText(Path.Combine(app, "MainWindow.axaml.cs"))
                     + File.ReadAllText(Path.Combine(app, "MainWindow.Session.cs"))
                     + File.ReadAllText(Path.Combine(app, "MainWindow.Recovery.cs"));
        var services = File.ReadAllText(Path.Combine(app, "AppServices.cs"));

        Check(results.Contains("DescribeNoticeSummary", StringComparison.Ordinal), "cap081-summary-is-visible");
        Check(shell.Contains("Recover _Unsaved Scripts", StringComparison.Ordinal), "cap046-menu");
        Check(window.Contains("DocumentRecovery", StringComparison.Ordinal), "cap046-window-uses-recovery");
        Check(window.Contains("OfferSummary", StringComparison.Ordinal), "cap046-offer-uses-core-text");
        var recover = MethodBody(window, "void OnRecoverUnsaved");
        Check(recover.Contains("Clear", StringComparison.Ordinal) && recover.Contains("Discard", StringComparison.Ordinal), "cap046-discard-is-explicit");
        Check(shell.Contains("Execution _History", StringComparison.Ordinal), "cap064-menu");
        Check(window.Contains("ExecutionHistory.FromResult", StringComparison.Ordinal), "cap064-records-finished-batch");
        var recall = MethodBody(window, "void OnExecutionHistory");
        Check(recall.Contains("ScriptForEditor", StringComparison.Ordinal) && !recall.Contains("ExecuteAsync", StringComparison.Ordinal), "cap064-recall-does-not-run");
        Check(results.Contains("PresentColumn", StringComparison.Ordinal), "cap091-grid-uses-binary-presentation");
        Check(services.Contains("DocumentRecoveryStore", StringComparison.Ordinal)
              && services.Contains("ExecutionHistoryStore", StringComparison.Ordinal), "cap046-cap064-stores-are-app-services");
        Pass("more-caps-wiring");
    }

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
}
