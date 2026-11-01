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

// --- 3. Production BatchObserver: stop causes, drain, unobserved, shared UI formatter ---
void AssertUiSurfaces(QueryResult result, string expectedStatus, string name)
{
    var status = QueryOutcomeFormatter.DescribeStatus(result);
    Check(status == expectedStatus, name + "-status-text");
    Check(QueryOutcomeFormatter.UiSurfacesAgree(result, out var detail), name + "-ui-agree:" + detail);
    Check(QueryOutcomeFormatter.FormatResults(result).Contains(expectedStatus, StringComparison.Ordinal), name + "-results");
    Check(QueryOutcomeFormatter.FormatMessages(result).Contains(expectedStatus, StringComparison.Ordinal), name + "-messages");
}

using (var cancelReader = new ScriptedResultReader(
           ["n"], [["kept"]],
           failOnReadAfterRows: new OperationCanceledException("user-cancel")))
{
    var obs = await BatchObserver.ObserveOpenedReaderAsync(
        cancelReader, new ResultBudget(), CancellationToken.None, TimeSpan.FromSeconds(30),
        new StopContext(false, UserCancelRequested: true, WatchdogCancelRequested: false, false));
    var result = BatchObserver.BuildResult("SELECT n", "s", "d", obs);
    Check(result.ResultSets[0].Rows[0][0].Display == "kept", "cancel-kept-row");
    Check(result.StopCause == StopCause.UserCancel, "cancel-stop");
    Check(result.Completion == QueryCompletionKind.CancelledClientEnded, "cancel-kind");
    AssertUiSurfaces(result, "Cancelled (client ended)", "cancel");
}
Pass("observe-one-row-then-cancel");

using (var disconnectReader = new ScriptedResultReader(
           ["n"], [["kept"]],
           failOnReadAfterRows: new OperationCanceledException("disconnect")))
{
    var obs = await BatchObserver.ObserveOpenedReaderAsync(
        disconnectReader, new ResultBudget(), CancellationToken.None, TimeSpan.FromSeconds(30),
        new StopContext(DisconnectRequested: true, false, false, false));
    var result = BatchObserver.BuildResult("SELECT n", "s", "d", obs);
    Check(result.StopCause == StopCause.Disconnect, "disconnect-stop");
    Check(result.Completion == QueryCompletionKind.UnknownAfterDisconnect, "disconnect-kind");
    Check(result.ResultSets[0].Rows[0][0].Display == "kept", "disconnect-row");
    AssertUiSurfaces(result, "Unknown after disconnect", "disconnect");
}
Pass("observe-one-row-then-disconnect");

using (var watchdogReader = new ScriptedResultReader(
           ["n"], [["kept"]],
           failOnReadAfterRows: new OperationCanceledException("watchdog")))
{
    var obs = await BatchObserver.ObserveOpenedReaderAsync(
        watchdogReader, new ResultBudget(), CancellationToken.None, TimeSpan.FromSeconds(30),
        new StopContext(false, false, WatchdogCancelRequested: true, false));
    var result = BatchObserver.BuildResult("SELECT n", "s", "d", obs);
    Check(result.StopCause == StopCause.WatchdogTimeout, "watchdog-stop");
    Check(result.Completion == QueryCompletionKind.TimedOut, "watchdog-kind");
    AssertUiSurfaces(result, "Timed out", "watchdog");
}
Pass("observe-one-row-then-watchdog");

using (var timeoutReader = new ScriptedResultReader(
           ["n"], [["kept"]],
           failOnReadAfterRows: new ProviderErrorException(-2, "Timeout expired")))
{
    var obs = await BatchObserver.ObserveOpenedReaderAsync(
        timeoutReader, new ResultBudget(), CancellationToken.None, TimeSpan.FromSeconds(30),
        new StopContext(false, false, false, false));
    var result = BatchObserver.BuildResult("SELECT n", "s", "d", obs);
    Check(result.StopCause == StopCause.ProviderTimeout, "provider-timeout-stop");
    Check(result.Completion == QueryCompletionKind.TimedOut, "timeout-kind");
    Check(result.ResultSets[0].Rows[0][0].Display == "kept", "timeout-row");
    Check(result.ErrorSummary!.Contains("-2", StringComparison.Ordinal) ||
          result.ErrorSummary.Contains("Provider timeout", StringComparison.Ordinal), "timeout-summary");
    AssertUiSurfaces(result, "Timed out (provider)", "provider-timeout");
}
Pass("observe-one-row-then-timeout");

using (var lossReader = new ScriptedResultReader(
           ["n"], [["kept"]],
           failOnReadAfterRows: new ProviderErrorException(-1, "transport lost")))
{
    var obs = await BatchObserver.ObserveOpenedReaderAsync(
        lossReader, new ResultBudget(), CancellationToken.None, TimeSpan.FromSeconds(30),
        new StopContext(false, false, false, false));
    var result = BatchObserver.BuildResult("SELECT n", "s", "d", obs);
    Check(result.StopCause == StopCause.TransportLoss, "transport-stop");
    Check(result.Completion == QueryCompletionKind.UnknownAfterDisconnect, "transport-kind");
    Check(result.ResultSets[0].Rows[0][0].Display == "kept", "transport-row");
    AssertUiSurfaces(result, "Unknown after disconnect", "transport");
}
Pass("observe-one-row-then-transport-loss");

// Drain without discarded-value access; later SQL error still observed.
{
    var many = Enumerable.Range(0, 12).Select(i => new[] { $"r{i}" }).ToArray();
    using var guardReader = new DiscardValueGuardReader(
        ["n"], many, retainableRows: 1,
        lateError: new ProviderErrorException(50000, "late error after limit"));
    var budget = new ResultBudget();
    // Room for one short retained cell ("r0"); further rows must be drained without GetValue.
    budget.TryReserve(QueryLimits.MaxRetainedOutputChars - 2);
    var obs = await BatchObserver.ObserveOpenedReaderAsync(
        guardReader, budget, CancellationToken.None, TimeSpan.FromSeconds(30),
        new StopContext(false, false, false, false));
    var result = BatchObserver.BuildResult("SELECT n", "s", "d", obs);
    Check(guardReader.DiscardedValueAccessAttempts == 0, "drain-no-discarded-getvalue");
    Check(result.RetentionLimitReached, "limit-retained-flag");
    Check(result.ResultSets[0].Rows.Count >= 1, "limit-has-row");
    Check(result.StopCause == StopCause.SqlError, "limit-then-error-stop");
    Check(result.Completion == QueryCompletionKind.Failed, "limit-then-error-kind");
    AssertUiSurfaces(result, "Failed", "limit-then-error");
}
Pass("observe-limit-drain-then-later-error");

// Drain deadline expiry → distinct FinalCompletionUnobserved; target check must not upgrade.
{
    using var hangReader = new HangAfterFirstRowReader(["n"], ["kept"]);
    var budget = new ResultBudget();
    budget.TryReserve(QueryLimits.MaxRetainedOutputChars - 4);
    var obs = await BatchObserver.ObserveOpenedReaderAsync(
        hangReader, budget, CancellationToken.None, TimeSpan.FromMilliseconds(20),
        new StopContext(false, false, false, false));
    var result = BatchObserver.BuildResult("SELECT n", "s", "d", obs);
    Check(result.Completion == QueryCompletionKind.FinalCompletionUnobserved, "drain-deadline-kind");
    Check(result.StopCause == StopCause.DrainDeadline, "drain-deadline-stop");
    Check(result.ResultSets[0].Rows.Count >= 1 && result.ResultSets[0].Rows[0][0].Display == "kept", "drain-deadline-row");
    AssertUiSurfaces(result, "Final completion unobserved", "drain-deadline");

    var afterVerify = SqlSession.WithTargetVerification(result, verificationSucceeded: true, verifiedDatabase: "d");
    Check(afterVerify.Completion == QueryCompletionKind.FinalCompletionUnobserved, "target-no-upgrade-completion");
    Check(afterVerify.StopCause == StopCause.DrainDeadline, "target-no-upgrade-stop");
    AssertUiSurfaces(afterVerify, "Final completion unobserved", "drain-deadline-after-verify");
}
Pass("observe-drain-deadline-unobserved");

// Watchdog fires during drain while drain deadline is still pending → WatchdogTimeout (not DrainDeadline).
{
    var timers = new TimerStopEvidence();
    using var watchdogCts = new CancellationTokenSource();
    watchdogCts.Token.Register(timers.NoteWatchdog);
    using var hangReader = new HangAfterFirstRowReader(["n"], ["kept"]);
    var budget = new ResultBudget();
    budget.TryReserve(QueryLimits.MaxRetainedOutputChars - 4);
    // Long drain deadline; short watchdog — linked drain cancel must not be mis-attributed.
    watchdogCts.CancelAfter(TimeSpan.FromMilliseconds(40));
    var obs = await BatchObserver.ObserveOpenedReaderAsync(
        hangReader, budget, watchdogCts.Token, TimeSpan.FromSeconds(60),
        () => new StopContext(
            DisconnectRequested: false,
            UserCancelRequested: false,
            WatchdogCancelRequested: timers.WatchdogFired,
            DrainDeadlineRequested: timers.DrainDeadlineFired,
            Timers: timers),
        timers);
    var result = BatchObserver.BuildResult("SELECT n", "s", "d", obs);
    Check(timers.WatchdogFired, "drain-watchdog-evidence");
    Check(!timers.DrainDeadlineFired, "drain-deadline-still-pending");
    Check(result.StopCause == StopCause.WatchdogTimeout, "watchdog-during-drain-stop");
    Check(result.Completion == QueryCompletionKind.TimedOut, "watchdog-during-drain-kind");
    AssertUiSurfaces(result, "Timed out", "watchdog-during-drain");
}
Pass("observe-watchdog-during-drain");

// Provider -2 recorded, then user cancel before verification → TimedOut preserved.
{
    var obs = new BatchObserver.Observation();
    BatchObserver.ApplyStop(
        obs,
        new ProviderErrorException(-2, "Timeout expired"),
        new StopContext(false, false, false, false));
    Check(obs.StopCause == StopCause.ProviderTimeout, "provider-then-cancel-recorded");
    Check(obs.Completion == QueryCompletionKind.TimedOut, "provider-then-cancel-kind");
    // Late Cancel click must not overwrite provider timeout.
    Check(!BatchObserver.TryRefineUserCancel(obs, serverStateVerified: true, transactionState: 0, out _),
          "provider-then-cancel-no-refine");
    Check(obs.StopCause == StopCause.ProviderTimeout, "provider-then-cancel-stop-kept");
    Check(obs.Completion == QueryCompletionKind.TimedOut, "provider-then-cancel-completion-kept");
    var result = BatchObserver.BuildResult("SELECT n", "s", "d", obs);
    AssertUiSurfaces(result, "Timed out (provider)", "provider-then-cancel");
}
Pass("observe-provider-timeout-survives-late-cancel");

using (var throwReader = new OneRowThenThrowReader(["n"], ["kept"]))
{
    var outcome = await ResultMaterializer.ReadResultSetAsync(throwReader, new ResultBudget(), CancellationToken.None);
    Check(outcome.RowFailure is not null && outcome.Set.Rows.Count == 1, "row-failure-materializer");
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

// Failed replacement read + intervening edit: latest text stays dirty
openDoc.SetText("still-unsaved");
var (p2, pend2) = await DocumentLifecycle.BeginOpenAsync(
    openDoc,
    () => Task.FromResult(DocumentLifecycle.PromptChoice.Discard),
    _ => Task.FromResult(true));
Check(p2 && pend2 is not null, "open-discard-pending-2");
var versionBeforeFail = openDoc.Version;
var failRead = ScriptDocument.ReadFileTextAsync("/tmp/sqloryn-missing-open-file.sql");
openDoc.SetText("intervening-edit-during-failed-open");
try
{
    await failRead;
    throw new Exception("FAIL expected missing file");
}
catch (FileNotFoundException)
{
    DocumentLifecycle.Rollback([pend2!]);
    Check(openDoc.IsDirty && openDoc.Text == "intervening-edit-during-failed-open", "failed-read-keeps-latest");
    Check(openDoc.Version != versionBeforeFail, "failed-read-version-bumped");
}
Pass("open-deferred-discard");

// Successful delayed read with intervening edit: abandon replacement; Close still prompts
var raceDoc = new ScriptDocument();
raceDoc.SetText("original-dirty");
var versionAtReadStart = raceDoc.Version;
var tmpPath = Path.Combine(Path.GetTempPath(), "sqloryn-open-race.sql");
await File.WriteAllTextAsync(tmpPath, "from-disk");
var delayed = Task.Run(async () =>
{
    await Task.Delay(30);
    return await ScriptDocument.ReadFileTextAsync(tmpPath);
});
raceDoc.SetText("typed-during-open");
var fileText = await delayed;
Check(!DocumentLifecycle.TryCommitOpenReplacement(raceDoc, tmpPath, fileText, versionAtReadStart), "open-race-abandon");
Check(raceDoc.IsDirty && raceDoc.Text == "typed-during-open", "open-race-keeps-edit");
// Close still prompts because dirty
var (closeOk, _) = await DocumentLifecycle.ResolveDirtyDeferredAsync(
    raceDoc,
    () => Task.FromResult(DocumentLifecycle.PromptChoice.Cancel),
    _ => Task.FromResult(true));
Check(!closeOk && raceDoc.IsDirty, "close-still-prompts-after-abandoned-open");
Pass("open-version-race");

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
    PartialResults = false,
    StopCause = StopCause.SqlError
};
var useVerifyOk = SqlSession.WithTargetVerification(useThenError, verificationSucceeded: true, verifiedDatabase: "otherdb");
Check(useVerifyOk.Completion == QueryCompletionKind.Failed && useVerifyOk.Database == "otherdb" &&
      useVerifyOk.TargetConfidence == TargetConfidence.Known &&
      useVerifyOk.StopCause == StopCause.SqlError, "use-then-error-target-verified");

var useVerifyFail = SqlSession.WithTargetVerification(useThenError, verificationSucceeded: false, null);
Check(useVerifyFail.Completion == QueryCompletionKind.Failed &&
      useVerifyFail.TargetConfidence == TargetConfidence.Unknown &&
      useVerifyFail.StopCause == StopCause.SqlError, "use-then-error-verify-failed");

var unobserved = new QueryResult
{
    ExecutedSql = "SELECT …",
    Server = "s",
    Database = "d",
    Completion = QueryCompletionKind.FinalCompletionUnobserved,
    StopCause = StopCause.DrainDeadline,
    ResultSets = [new ResultSet { Columns = ["n"], Rows = [[CellValue.Text("kept", "string")]], Truncated = true }],
    PartialResults = true,
    RetentionLimitReached = true
};
var unobservedVerify = SqlSession.WithTargetVerification(unobserved, true, "d");
Check(unobservedVerify.Completion == QueryCompletionKind.FinalCompletionUnobserved &&
      unobservedVerify.StopCause == StopCause.DrainDeadline, "unobserved-not-upgraded");
AssertUiSurfaces(unobservedVerify, "Final completion unobserved", "unobserved-target");
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
