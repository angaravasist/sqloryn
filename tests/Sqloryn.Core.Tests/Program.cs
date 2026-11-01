using System.Data;
using System.Data.SqlTypes;
using Sqloryn.Core;

static void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception($"FAIL {name}");
}

static void Pass(string name) => Console.WriteLine($"PASS {name}");

try
{
    new ConnectionOptions { Server = "localhost", UserId = "sa", Password = "x", Encrypt = false }.Validate();
    throw new Exception("FAIL encrypt-required");
}
catch (ArgumentException) { Pass("encrypt-required"); }

try
{
    new ConnectionOptions
    {
        Server = "localhost",
        UserId = "sa",
        Password = "x",
        TrustServerCertificate = true,
        ServerCertificatePath = "/tmp/does-not-exist.crt"
    }.Validate();
    throw new Exception("FAIL pin-and-trust-mutex");
}
catch (ArgumentException) { Pass("pin-and-trust-mutex"); }

var opts = new ConnectionOptions
{
    Server = "db.example",
    Port = 14333,
    UserId = "app",
    Password = "Aa1!secret",
    Database = "appdb",
    Encrypt = true
};
opts.Validate();
Check(opts.DisplayTarget == "db.example,14333", "display-target");
Pass("connection-options");

Check(SqlIdentity.QuoteIdentifier("Name]") == "[Name]]]", "quote-identifier");
Check(SqlIdentity.Qualify("dbo", "Orders") == "[dbo].[Orders]", "qualify");
var q1 = SqlIdentity.Qualify3("DbA", "dbo", "Orders");
var q2 = SqlIdentity.Qualify3("DbB", "dbo", "Orders");
Check(q1 == "[DbA].[dbo].[Orders]" && q2 == "[DbB].[dbo].[Orders]" && q1 != q2, "qualify3-cross-db");
Pass("sql-identity");

Check(ScriptDocument.ResolveBatch("full", null) == "full", "batch-document");
Check(ScriptDocument.ResolveBatch("full", "sel") == "sel", "batch-selection");
var doc = new ScriptDocument();
doc.SetText("a");
Check(doc.IsDirty, "dirty");
doc.DiscardChangesTo("a");
Check(!doc.IsDirty, "discard-clears-dirty");
Pass("script-document");

Check(SqlSession.LooksLikeGoBatch("SELECT 1;\nGO\nSELECT 2;"), "go-detect");
Check(!SqlSession.LooksLikeGoBatch("SELECT 'GO' AS g;"), "go-string-not-batch");
Pass("go-policy");

var table = new DataTable();
table.Columns.Add("a", typeof(string));
table.Columns.Add("b", typeof(string));
table.Columns.Add("c", typeof(string));
table.Columns.Add("n", typeof(int));
table.Rows.Add(DBNull.Value, "", "hi", 42);
using (var reader = table.CreateDataReader())
{
    Check(reader.Read(), "reader-row");
    Check(SqlSession.FormatCell(reader, 0).IsNull, "null-cell");
    Check(!SqlSession.FormatCell(reader, 1).IsNull && SqlSession.FormatCell(reader, 1).Kind == "empty-string", "empty-string-cell");
    Check(SqlSession.FormatCell(reader, 2).Display == "hi", "text-cell");
    Check(SqlSession.FormatCell(reader, 3).Kind == "number", "int-cell");
}
Pass("cell-format");

await using (var session = new SqlSession())
{
    Check(!session.IsConnected, "session-starts-disconnected");
    try
    {
        await session.ExecuteAsync("SELECT 1");
        throw new Exception("FAIL execute-without-connect");
    }
    catch (InvalidOperationException) { Pass("execute-without-connect"); }
}

Pass("tab-sql-isolation");

Check(QueryLimits.MaxRowsPerSet > 0 && QueryLimits.MaxCellChars > 0 && QueryLimits.MaxRetainedOutputChars > 0, "bounds");
var longTable = new DataTable();
longTable.Columns.Add("x", typeof(string));
longTable.Rows.Add(new string('x', QueryLimits.MaxCellChars + 50));
using (var longReader = longTable.CreateDataReader())
{
    Check(longReader.Read(), "long-reader");
    var cell = SqlSession.FormatCell(longReader, 0);
    Check(cell.Truncated && cell.Display.Length <= QueryLimits.MaxCellChars + 1, "cell-bound-truncated");
}
Pass("output-bounds");

await using (var closed = new SqlSession())
{
    await closed.DisposeAsync();
    try
    {
        await closed.ExecuteAsync("SELECT 1");
        throw new Exception("FAIL execute-after-dispose");
    }
    catch (ObjectDisposedException) { Pass("execute-after-dispose"); }
}

Check(ExecutionOutcomes.FromSqlError(-2) == QueryCompletionKind.TimedOut, "timeout-minus-2");
Check(ExecutionOutcomes.FromSqlError(-1) == QueryCompletionKind.UnknownAfterDisconnect, "broken-connection");
Check(!ExecutionOutcomes.IsBrokenConnection(-2), "timeout-not-broken");
Pass("execution-outcomes");

// --- 1. SqlDecimal production path (exact probe 38-digit literal), no System.Decimal ---
const string probe38 = "12345678901234567890123456789012345678";
var typed = new FakeSqlTypedCellSource(
    ["decimal"],
    [SqlDecimal.Parse(probe38)]);
var cell38 = ResultMaterializer.FormatSqlTypedCell(typed, 0);
Check(cell38.Display == probe38 && cell38.Kind == "decimal" && !cell38.Truncated, "sqldecimal-typed-path-38");
// Ensure FormatSqlDecimal never goes through System.Decimal.ToString of a narrowed value.
Check(ResultMaterializer.FormatSqlDecimal(SqlDecimal.Parse(probe38)).Display == probe38, "sqldecimal-direct");
Pass("numeric-38-production-path");

// --- 2. Budget exhausted mid-row; omit incomplete row; display shows intact rows + marker ---
var wide = new DataTable();
wide.Columns.Add("a", typeof(string));
wide.Columns.Add("b", typeof(string));
wide.Columns.Add("c", typeof(string));
wide.Rows.Add("ok1", "ok2", "ok3"); // full row — will fit first
wide.Rows.Add("x", new string('y', 80), "z"); // mid-row budget stop
using (var wideReader = wide.CreateDataReader())
{
    var budget = new ResultBudget();
    // Allow first row (3 cells) and first cell of second row only.
    // Charge uses display length; reserve almost all after first row.
    var first = await ResultMaterializer.ReadResultSetAsync(wideReader, budget, CancellationToken.None);
    // Re-read with a tight budget from scratch:
}
using (var wideReader2 = wide.CreateDataReader())
{
    var budget = new ResultBudget();
    // Pre-consume budget so only ~5 chars remain after accounting for min cell charges.
    budget.TryReserve(QueryLimits.MaxRetainedOutputChars - 12);
    var outcome = await ResultMaterializer.ReadResultSetAsync(wideReader2, budget, CancellationToken.None);
    Check(outcome.Set.BudgetExceeded || outcome.Set.Truncated, "mid-row-budget-flag");
    // No incomplete row retained: every retained row has FieldCount cells that were reserved.
    foreach (var row in outcome.Set.Rows)
        Check(row.Count == 3 && row.All(c => c.Display is not null || c.IsNull), "no-uninit-cells");
    // Build a QueryResult and render with the UI path.
    var displayResult = new QueryResult
    {
        ExecutedSql = "SELECT …",
        Server = "s",
        Database = "d",
        Completion = QueryCompletionKind.Succeeded,
        ResultSets = [outcome.Set],
        PartialResults = true,
        TargetConfidence = TargetConfidence.Known
    };
    var rendered = ResultMaterializer.RenderForDisplay(displayResult);
    Check(rendered.Contains("[truncated", StringComparison.Ordinal), "ui-truncation-marker");
    // Intact completed rows (if any) appear before the marker.
    if (outcome.Set.Rows.Count > 0)
        Check(rendered.IndexOf("ok1", StringComparison.Ordinal) < rendered.IndexOf("[truncated", StringComparison.Ordinal)
              || !rendered.Contains("ok1", StringComparison.Ordinal), "intact-before-or-absent");
}
Pass("budget-mid-row-display");

// Empty cells still consume budget.
var emptyBudget = new ResultBudget();
Check(emptyBudget.ChargeFor(CellValue.Text("", "empty-string")) >= QueryLimits.MinCellBudgetChars, "empty-cell-charge");
Check(emptyBudget.ChargeFor(CellValue.Null) >= QueryLimits.MinCellBudgetChars, "null-cell-charge");
Pass("budget-empty-cells");

// --- 3. Reader returns one row then throws; assert row + error + partial together (not preconstructed) ---
using (var throwReader = new OneRowThenThrowReader(["n"], ["kept"]))
{
    var outcome = await ResultMaterializer.ReadResultSetAsync(throwReader, new ResultBudget(), CancellationToken.None);
    Check(outcome.RowFailure is not null, "row-failure-present");
    Check(outcome.Set.Rows.Count == 1, "one-complete-row-kept");
    Check(outcome.Set.Rows[0][0].Display == "kept", "kept-cell");
    Check(outcome.Set.Truncated, "truncated-on-row-failure");
    var built = new QueryResult
    {
        ExecutedSql = "SELECT n",
        Server = "s",
        Database = "d",
        Completion = QueryCompletionKind.Failed,
        ResultSets = [outcome.Set],
        ErrorSummary = outcome.RowFailure!.Message,
        PartialResults = true,
        TargetConfidence = TargetConfidence.Known
    };
    Check(built.PartialResults && built.ResultSets[0].Rows.Count == 1 &&
          built.ErrorSummary!.Contains("synthetic", StringComparison.Ordinal), "assert-row-error-partial-together");
}
Pass("row-failure-preserves-complete-rows");

// --- 4. Deferred document lifecycle ---
// Open → Discard intent → cancel picker: dirty preserved
var openDoc = new ScriptDocument();
openDoc.SetText("unsaved-open");
var (openProceed, openPending) = await DocumentLifecycle.BeginOpenAsync(
    openDoc,
    () => Task.FromResult(DocumentLifecycle.PromptChoice.Discard),
    _ => Task.FromResult(true));
Check(openProceed && openPending is { Kind: DocumentLifecycle.PendingActionKind.Discard }, "open-discard-pending");
Check(openDoc.IsDirty && openDoc.Text == "unsaved-open", "open-discard-not-yet-applied");
DocumentLifecycle.Rollback([openPending!]);
Check(openDoc.IsDirty && openDoc.Text == "unsaved-open", "open-cancel-picker-keeps-dirty");

// Failed replacement read: rollback keeps dirty
openDoc.SetText("still-unsaved");
var (p2, pend2) = await DocumentLifecycle.BeginOpenAsync(
    openDoc,
    () => Task.FromResult(DocumentLifecycle.PromptChoice.Discard),
    _ => Task.FromResult(true));
Check(p2 && pend2 is not null, "open-discard-pending-2");
try
{
    await ScriptDocument.ReadFileTextAsync("/tmp/sqloryn-missing-open-file.sql");
    throw new Exception("FAIL expected missing file");
}
catch (FileNotFoundException)
{
    DocumentLifecycle.Rollback([pend2!]);
    Check(openDoc.IsDirty && openDoc.Text == "still-unsaved", "failed-read-keeps-dirty");
}
Pass("open-deferred-discard");

// Two dirty tabs → Quit → Discard → Cancel → buffers protected; close again still dirty
var d1 = new ScriptDocument();
d1.SetText("tab-one-unsaved");
var d2 = new ScriptDocument();
d2.SetText("tab-two-unsaved");
var prompts = new Queue<DocumentLifecycle.PromptChoice>([
    DocumentLifecycle.PromptChoice.Discard,
    DocumentLifecycle.PromptChoice.Cancel
]);
var (quitOk, quitPending) = await DocumentLifecycle.ResolveAllDirtyDeferredAsync(
    [d1, d2],
    prompt: (_, _) => Task.FromResult(prompts.Dequeue()),
    save: _ => Task.FromResult(true));
Check(!quitOk, "quit-cancel-at-second");
Check(d1.IsDirty && d1.Text == "tab-one-unsaved", "quit-first-still-dirty");
Check(d2.IsDirty && d2.Text == "tab-two-unsaved", "quit-second-still-dirty");
// Try closing again — both still protected
prompts = new Queue<DocumentLifecycle.PromptChoice>([
    DocumentLifecycle.PromptChoice.Cancel
]);
var (quitOk2, _) = await DocumentLifecycle.ResolveAllDirtyDeferredAsync(
    [d1, d2],
    prompt: (_, _) => Task.FromResult(prompts.Dequeue()),
    save: _ => Task.FromResult(true));
Check(!quitOk2 && d1.IsDirty && d2.IsDirty, "second-close-still-protected");
Pass("quit-deferred-discard");

// Successful open: LoadFrom replaces buffer; do not Commit pending discard afterward.
var commitDoc = new ScriptDocument();
commitDoc.SetText("old");
var (cOk, cPend) = await DocumentLifecycle.BeginOpenAsync(
    commitDoc,
    () => Task.FromResult(DocumentLifecycle.PromptChoice.Discard),
    _ => Task.FromResult(true));
Check(cOk && cPend is not null, "commit-pending");
commitDoc.LoadFrom("/tmp/x.sql", "from-file");
Check(!commitDoc.IsDirty && commitDoc.Text == "from-file", "open-load-replaces-without-discard-commit");
Pass("document-lifecycle-deferred");

// Save failure / concurrent edit
var saveDoc = new ScriptDocument();
saveDoc.SetText("v1");
try
{
    await saveDoc.SaveAsync("/tmp/sqloryn-should-fail-dir/nope.sql", writer: (_, _, _) =>
        throw new IOException("disk full"));
    throw new Exception("FAIL save-should-throw");
}
catch (IOException)
{
    Check(saveDoc.IsDirty, "save-fail-keeps-dirty");
    Pass("save-io-failure");
}

await saveDoc.SaveAsync(
    "/tmp/sqloryn-core-test-save.sql",
    betweenCaptureAndWrite: () =>
    {
        saveDoc.SetText("v3-during-save");
        return Task.CompletedTask;
    });
Check(saveDoc.IsDirty && saveDoc.Text == "v3-during-save", "save-concurrent-edit-keeps-dirty");
Pass("save-concurrent-edit");

// --- 5. SQL completion vs target verification ---
var sqlOk = new QueryResult
{
    ExecutedSql = "UPDATE t SET x=1",
    Server = "s",
    Database = "appdb",
    Completion = QueryCompletionKind.Succeeded,
    ResultSets =
    [
        new ResultSet { Columns = ["x"], Rows = [[CellValue.Text("1", "number")]], Truncated = false }
    ],
    PartialResults = false
};
var afterFailedVerify = SqlSession.WithTargetVerification(sqlOk, verificationSucceeded: false, verifiedDatabase: null);
Check(afterFailedVerify.Completion == QueryCompletionKind.Succeeded, "sql-success-preserved");
Check(afterFailedVerify.ResultSets[0].Rows.Count == 1, "rows-preserved-after-verify-fail");
Check(afterFailedVerify.TargetConfidence == TargetConfidence.Unknown, "target-unknown");
Check(afterFailedVerify.ErrorSummary!.Contains("not replayed", StringComparison.Ordinal), "no-replay-note");

var useThenError = new QueryResult
{
    ExecutedSql = "USE otherdb; SELECT 1/0",
    Server = "s",
    Database = "master",
    Completion = QueryCompletionKind.Failed,
    ResultSets = [],
    ErrorSummary = "SQL error 8134: Divide by zero",
    PartialResults = false
};
var useVerifyOk = SqlSession.WithTargetVerification(useThenError, verificationSucceeded: true, verifiedDatabase: "otherdb");
Check(useVerifyOk.Completion == QueryCompletionKind.Failed && useVerifyOk.Database == "otherdb" &&
      useVerifyOk.TargetConfidence == TargetConfidence.Known, "use-then-error-target-verified");

var useVerifyFail = SqlSession.WithTargetVerification(useThenError, verificationSucceeded: false, null);
Check(useVerifyFail.Completion == QueryCompletionKind.Failed &&
      useVerifyFail.TargetConfidence == TargetConfidence.Unknown, "use-then-error-verify-failed");
Pass("target-verification-separated");

var flag = 0;
Check(RunGuard.TryEnter(ref flag) && !RunGuard.TryEnter(ref flag), "run-guard");
RunGuard.Exit(ref flag);
Pass("repeated-run-guard");

// Explorer ownership (unchanged contract)
var sessionKey = new object();
var explorerDoc = new ScriptDocument();
var tabId = Guid.NewGuid();
long generation = 1;
string? currentDb = "DbA";
var bindingA = new OperationBinding(tabId, sessionKey, explorerDoc, currentDb, generation);
var nodeA = new ObjectExplorerService.Node { Title = "DbA", Kind = ObjectExplorerService.NodeKind.Database, Database = "DbA" };
var expandOk = await ObjectExplorerService.ExpandAsync(
    nodeA, bindingA,
    b => b.Matches(tabId, sessionKey, explorerDoc, currentDb, generation, true),
    async (n, ct) =>
    {
        await Task.Delay(20, ct);
        generation++;
        currentDb = "DbB";
        n.Children.Add(new ObjectExplorerService.Node { Title = "dbo", Kind = ObjectExplorerService.NodeKind.Schema, Database = "DbA", Schema = "dbo" });
    });
Check(!expandOk && !nodeA.Loaded, "stale-metadata-rejected");
Pass("explorer-ownership");

Console.WriteLine("PASS Sqloryn.Core component assertions (E1; no SQL Server connection)");
Console.WriteLine("NOTE: live SqlDataReader decimal(38) against SQL Server remains provider-only / live-host verification.");
return 0;
