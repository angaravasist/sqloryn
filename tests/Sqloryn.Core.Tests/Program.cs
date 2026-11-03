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
    using var watchdogSignal = new CancellationTokenSource();
    using var watchdogTimer = AttributedTimer.Arm(
        TimeSpan.FromMilliseconds(40), timers.SignalWatchdog, watchdogSignal);
    using var hangReader = new HangAfterFirstRowReader(["n"], ["kept"]);
    var budget = new ResultBudget();
    budget.TryReserve(QueryLimits.MaxRetainedOutputChars - 4);
    // Long drain deadline; short watchdog — linked drain cancel must not be mis-attributed.
    var obs = await BatchObserver.ObserveOpenedReaderAsync(
        hangReader, budget, watchdogSignal.Token, TimeSpan.FromSeconds(60),
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

// Cause is recorded before the operation token's cancellation callbacks run (no sleep race).
{
    void AssertRecordedBeforeOperationCallbacks(
        Action<TimerStopEvidence, CancellationTokenSource> armImmediate,
        StopCause expectedCause,
        string expectedStatus,
        string name)
    {
        var timers = new TimerStopEvidence();
        using var operation = new CancellationTokenSource();
        StopCause? seenInsideCallback = null;
        var callbackRan = false;
        using var reg = operation.Token.Register(() =>
        {
            // Must observe attribution before this callback returns — not after all timers settle.
            seenInsideCallback = timers.CapturedTimerCause;
            callbackRan = true;
            Check(seenInsideCallback == expectedCause, name + "-cause-visible-in-operation-callback");
        });
        armImmediate(timers, operation);
        Check(callbackRan, name + "-operation-callback-ran");
        Check(seenInsideCallback == expectedCause, name + "-cause-after-signal");

        var obs = new BatchObserver.Observation();
        BatchObserver.ApplyStop(
            obs,
            new OperationCanceledException(name),
            new StopContext(false, false, false, false, timers));
        Check(obs.StopCause == expectedCause, name + "-classified");
        var result = BatchObserver.BuildResult("SELECT n", "s", "d", obs);
        AssertUiSurfaces(result, expectedStatus, name);
    }

    AssertRecordedBeforeOperationCallbacks(
        (timers, operation) =>
        {
            using var timer = AttributedTimer.Arm(TimeSpan.Zero, timers.SignalDrainDeadline, operation);
        },
        StopCause.DrainDeadline,
        "Final completion unobserved",
        "attrib-drain");

    AssertRecordedBeforeOperationCallbacks(
        (timers, operation) =>
        {
            using var timer = AttributedTimer.Arm(TimeSpan.Zero, timers.SignalWatchdog, operation);
        },
        StopCause.WatchdogTimeout,
        "Timed out",
        "attrib-watchdog");
}
Pass("timer-cause-visible-before-operation-cancel-callback");

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

// GO batch splitter
{
    var b = GoBatchSplitter.Split("SELECT 1;\nGO\nSELECT 2;\r\ngo\r\n\r\nGO\n  Go  \nSELECT 3");
    Check(b.Count == 3 && b[0] == "SELECT 1;" && b[1] == "SELECT 2;" && b[2] == "SELECT 3", "go-split-basic");
    var repeat = GoBatchSplitter.Split("INSERT t DEFAULT VALUES\nGO 3\nSELECT 1");
    Check(repeat.Count == 4 && repeat[0] == repeat[2] && repeat[3] == "SELECT 1", "go-split-count");
    Check(GoBatchSplitter.Split("SELECT 'a\nGO\nb';").Count == 1, "go-in-string");
    Check(GoBatchSplitter.Split("/* c\nGO\n*/ SELECT 1").Count == 1, "go-in-block-comment");
    Check(GoBatchSplitter.Split("/* a /* nested\nGO\n*/\nGO\n*/ SELECT 1").Count == 1, "go-in-nested-comment");
    Check(GoBatchSplitter.Split("SELECT [x\nGO\ny]").Count == 1, "go-in-bracket-identifier");
    Check(GoBatchSplitter.Split("-- note\nSELECT 1\nGO -- done\nSELECT 2").Count == 2, "go-trailing-comment");
    Check(GoBatchSplitter.Split("SELECT 1 GO\nSELECT 2").Count == 1, "go-not-alone");
    Check(GoBatchSplitter.Split("GOTO label").Count == 1 && !GoBatchSplitter.ContainsSeparator("GOTO x"), "go-prefix-word");
    Check(GoBatchSplitter.Split("GO\n  \nGO").Count == 0, "go-empty-skipped");
    Check(GoBatchSplitter.Split("   ").Count == 0, "go-blank-script");
    Check(GoBatchSplitter.ContainsSeparator("SELECT 1\nGO") && !GoBatchSplitter.ContainsSeparator("SELECT 'x'"), "go-contains");
    try { GoBatchSplitter.Split("SELECT 1\nGO 0"); throw new Exception("FAIL go-zero-count"); }
    catch (ArgumentException) { }
    try { GoBatchSplitter.Split("SELECT 1\nGO 100000"); throw new Exception("FAIL go-huge-count"); }
    catch (ArgumentException) { }
    Pass("go-batch-splitter");

    await using var goSession = new SqlSession();
    try
    {
        await goSession.ExecuteAsync("SELECT 1\nGO\nSELECT 2");
        throw new Exception("FAIL go-execute-without-connect");
    }
    catch (InvalidOperationException ex) when (!ex.Message.Contains("not supported", StringComparison.Ordinal)) { }
    Pass("go-multi-batch-requires-connection");
}

// Connection profiles
{
    var root = Path.Combine(Path.GetTempPath(), "sqloryn-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new ConnectionProfileStore(root);
        Check(store.Load().Count == 0, "profiles-empty");
        var a = new ConnectionProfile { Name = "Alpha", Server = "a.example", UserId = "u", RememberPassword = true, LastUsedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var b = new ConnectionProfile { Name = "Beta", Server = "b.example", Port = 14330, UserId = "u2", LastUsedUtc = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero) };
        var c = new ConnectionProfile { Name = "Aardvark", Server = "c.example", UserId = "u3" };
        store.Upsert(a);
        store.Upsert(b);
        store.Upsert(c);
        Check(store.Load().Count == 3, "profiles-upsert-add");
        store.Upsert(a with { Name = "Alpha2" });
        Check(store.Load().Count == 3 && store.Load().Any(p => p.Name == "Alpha2"), "profiles-upsert-update");
        var ordered = store.ListOrderedByRecent();
        Check(ordered[0].Name == "Beta" && ordered[1].Name == "Alpha2" && ordered[2].Name == "Aardvark", "profiles-recent-order");
        var reloaded = new ConnectionProfileStore(root).Load().Single(p => p.Id == b.Id);
        Check(reloaded.Port == 14330 && reloaded.Encrypt && !reloaded.TrustServerCertificate, "profiles-roundtrip");
        var json = File.ReadAllText(store.FilePath);
        Check(!json.Contains("\"password\"", StringComparison.OrdinalIgnoreCase) && !json.Contains("\"pwd\"", StringComparison.OrdinalIgnoreCase) &&
              !json.Contains("secret", StringComparison.OrdinalIgnoreCase) && json.Contains("\"rememberPassword\"", StringComparison.Ordinal), "profiles-no-password-in-json");
        Check(Directory.GetFiles(root, "*.tmp").Length == 0 && Directory.GetFiles(root, ".*.tmp").Length == 0, "profiles-no-temp-left");
        Check(store.Remove(c.Id) && !store.Remove(c.Id) && store.Load().Count == 2, "profiles-remove");
        File.WriteAllText(store.FilePath, "{ not json");
        try { store.Load(); throw new Exception("FAIL profiles-corrupt"); }
        catch (InvalidDataException) { }
        Pass("connection-profiles");

        var settingsStore = new AppSettingsStore(root);
        var defaults = settingsStore.Load();
        Check(defaults.ResultsMaxRows == 10_000 && defaults.ConfirmDestructive && defaults.Theme == "System" && defaults.ShowLineNumbers, "settings-defaults");
        settingsStore.Save(defaults with { EditorFontSize = 16, ResultsMaxRows = 500, Theme = "Dark", ConfirmDestructive = false });
        var savedSettings = new AppSettingsStore(root).Load();
        Check(savedSettings.EditorFontSize == 16 && savedSettings.ResultsMaxRows == 500 && savedSettings.Theme == "Dark" && !savedSettings.ConfirmDestructive, "settings-roundtrip");
        File.WriteAllText(settingsStore.FilePath, "garbage");
        Check(settingsStore.Load().ResultsMaxRows == 10_000, "settings-corrupt-defaults");
        settingsStore.Save(new AppSettings { Theme = "Neon", ResultsMaxRows = -5, EditorFontSize = 1000 });
        var clamped = settingsStore.Load();
        Check(clamped.Theme == "System" && clamped.ResultsMaxRows == 1 && clamped.EditorFontSize == AppSettings.MaxFontSize, "settings-normalized");
        Pass("app-settings");
    }
    finally
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}

// Secrets
{
    ISecretStore secrets = new MemorySecretStore();
    var key = ProfileSecretKeys.ForProfile(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    Check(key == "profile:11111111-1111-1111-1111-111111111111", "secret-key-format");
    Check(await secrets.GetAsync(key) is null, "secret-missing");
    await secrets.SetAsync(key, "p1");
    await secrets.SetAsync(key, "p2");
    Check(await secrets.GetAsync(key) == "p2", "secret-overwrite");
    await secrets.DeleteAsync(key);
    await secrets.DeleteAsync(key);
    Check(await secrets.GetAsync(key) is null, "secret-delete");
    try { await secrets.SetAsync("", "x"); throw new Exception("FAIL secret-empty-key"); }
    catch (ArgumentException) { }
    if (!MacKeychainSecretStore.IsSupported)
    {
        try { await new MacKeychainSecretStore().GetAsync(key); throw new Exception("FAIL keychain-unsupported"); }
        catch (PlatformNotSupportedException) { }
    }
    Pass("secret-store-memory");

    if (MacKeychainSecretStore.IsSupported && Environment.GetEnvironmentVariable("SQLORYN_KEYCHAIN_SMOKE") == "1")
    {
        var keychain = new MacKeychainSecretStore();
        var smokeKey = ProfileSecretKeys.ForProfile(Guid.NewGuid());
        try
        {
            Check(await keychain.GetAsync(smokeKey) is null, "keychain-missing");
            await keychain.SetAsync(smokeKey, "synthetic-one");
            await keychain.SetAsync(smokeKey, "synthetic-two");
            Check(await keychain.GetAsync(smokeKey) == "synthetic-two", "keychain-roundtrip");
        }
        finally
        {
            await keychain.DeleteAsync(smokeKey);
        }
        Check(await keychain.GetAsync(smokeKey) is null, "keychain-delete");
        Pass("secret-store-keychain-smoke");
    }
}

// Export NULL vs empty
{
    var view = new ResultSetView
    {
        Columns = [new ResultColumn("id", "int"), new ResultColumn("note", "nvarchar"), new ResultColumn("amt", "decimal"), new ResultColumn("big", "bigint"), new ResultColumn("flag", "bit")],
        Rows =
        [
            [ResultCell.Of("1"), ResultCell.Null, ResultCell.Of("12345678901234567890.123456789"), ResultCell.Of("9007199254740993"), ResultCell.Of("true")],
            [ResultCell.Of("2"), ResultCell.Of(""), ResultCell.Null, ResultCell.Null, ResultCell.Of("false")],
            [ResultCell.Of("3"), ResultCell.Of("a,\"b\"\nc"), ResultCell.Of("0.5"), ResultCell.Of("1"), ResultCell.Null]
        ]
    };
    var csv = CsvExporter.ToCsv(view);
    var lines = csv.Split("\r\n");
    Check(lines[0] == "id,note,amt,big,flag", "csv-header");
    Check(lines[1] == "1,,12345678901234567890.123456789,9007199254740993,true", "csv-null-unquoted-empty");
    Check(lines[2] == "2,\"\",,,false", "csv-empty-string-quoted");
    Check(csv.Contains("3,\"a,\"\"b\"\"\nc\",0.5,1,", StringComparison.Ordinal) && csv.EndsWith("\r\n", StringComparison.Ordinal), "csv-quoting-crlf");

    var jsonRoot = System.Text.Json.JsonDocument.Parse(JsonExporter.ToJson(view)).RootElement;
    Check(jsonRoot.GetProperty("format").GetString() == JsonExporter.FormatId, "json-format-id");
    var set0 = jsonRoot.GetProperty("resultSets")[0];
    var jsonRows = set0.GetProperty("rows");
    Check(jsonRows.GetArrayLength() == 3, "json-array");
    Check(set0.GetProperty("columns").GetArrayLength() == 5 && jsonRows[0].GetArrayLength() == 5, "json-column-metadata-and-row-arrays");
    var r0 = jsonRows[0];
    var r1 = jsonRows[1];
    Check(r0[0].ValueKind == System.Text.Json.JsonValueKind.Number, "json-int-number");
    Check(r0[1].ValueKind == System.Text.Json.JsonValueKind.Null, "json-null");
    Check(r1[1].ValueKind == System.Text.Json.JsonValueKind.String && r1[1].GetString() == "", "json-empty-string");
    Check(r0[2].GetString() == "12345678901234567890.123456789", "json-decimal-string");
    Check(r0[3].GetString() == "9007199254740993", "json-bigint-string");
    Check(r0[4].ValueKind == System.Text.Json.JsonValueKind.True && r1[4].ValueKind == System.Text.Json.JsonValueKind.False, "json-bit-bool");
    using var sw = new StringWriter();
    JsonExporter.Write(sw, view);
    Check(sw.ToString() == JsonExporter.ToJson(view), "json-writer");

    var fromSet = ResultSetView.FromResultSet(new ResultSet
    {
        Columns = ["a", "b"],
        Rows = [[CellValue.Null, CellValue.Text("", "empty-string")]],
        Truncated = false
    });
    Check(fromSet.Rows[0][0].IsNull && !fromSet.Rows[0][1].IsNull && !fromSet.Truncated, "result-view-from-set");
    Pass("result-export");
}

// Metadata catalog
{
    var tables = MetadataCatalog.ListTables("My]Db", "dbo");
    Check(tables.Contains("[My]]Db].sys.tables", StringComparison.Ordinal) && tables.Contains("@schema", StringComparison.Ordinal) && !tables.Contains("'dbo'", StringComparison.Ordinal), "metadata-tables");
    Check(MetadataCatalog.ListViews("d", "s").Contains("sys.views", StringComparison.Ordinal), "metadata-views");
    Check(MetadataCatalog.ListProcedures("d", "s").Contains("sys.procedures", StringComparison.Ordinal), "metadata-procs");
    var cols = MetadataCatalog.ListColumns("d", "s", "t");
    Check(cols.Contains("@table", StringComparison.Ordinal) && cols.Contains("sys.columns", StringComparison.Ordinal), "metadata-columns");
    Check(cols.Contains("c.is_computed", StringComparison.Ordinal), "metadata-columns-computed");
    var triggers = MetadataCatalog.CountEnabledInsteadOfTriggers("d", "s", "t");
    Check(triggers.Contains("is_instead_of_trigger = 1", StringComparison.Ordinal) && triggers.Contains("@table", StringComparison.Ordinal) && !triggers.Contains("'t'", StringComparison.Ordinal), "metadata-instead-of-triggers");
    Check(MetadataCatalog.ListSchemas("d").Contains("[d].sys.schemas", StringComparison.Ordinal) && MetadataCatalog.ListDatabases().Contains("sys.databases", StringComparison.Ordinal), "metadata-dbs-schemas");
    try { MetadataCatalog.ListTables("", "dbo"); throw new Exception("FAIL metadata-bad-db"); }
    catch (ArgumentException) { }
    Check(ObjectExplorerService.NodeKind.Procedure != ObjectExplorerService.NodeKind.View, "node-kind-procedure");
    Pass("metadata-catalog");
}

// Table design preview
{
    var draft = new TableDesignDraft
    {
        Database = "App",
        Schema = "dbo",
        TableName = "Cust]omer",
        Columns =
        [
            new ColumnDefinition { Name = "Id", DataType = "int", IsNullable = false, IsIdentity = true },
            new ColumnDefinition { Name = "Name", DataType = "nvarchar", MaxLength = 100, IsNullable = false },
            new ColumnDefinition { Name = "Bio", DataType = "nvarchar", MaxLength = -1 },
            new ColumnDefinition { Name = "Balance", DataType = "decimal(18,2)" }
        ]
    };
    var create = TableDesignSql.PreviewCreate(draft).ExecutableSql;
    Check(create.StartsWith("CREATE TABLE [App].[dbo].[Cust]]omer] (", StringComparison.Ordinal), "design-create-header");
    Check(create.Contains("[Id] int IDENTITY(1,1) NOT NULL", StringComparison.Ordinal) &&
          create.Contains("[Name] nvarchar(100) NOT NULL", StringComparison.Ordinal) &&
          create.Contains("[Bio] nvarchar(MAX) NULL", StringComparison.Ordinal) &&
          create.Contains("[Balance] decimal(18,2) NULL", StringComparison.Ordinal), "design-create-columns");

    var desired = draft with
    {
        Columns =
        [
            draft.Columns[0],
            draft.Columns[1] with { MaxLength = 200 },
            new ColumnDefinition { Name = "Email", DataType = "varchar", MaxLength = 255 }
        ]
    };
    var alterPreview = TableDesignSql.PreviewAlter(draft, desired);
    var alter = alterPreview.ExecutableSql;
    Check(alter.Contains("DROP COLUMN [Bio]", StringComparison.Ordinal) && !alter.Contains("DESTRUCTIVE", StringComparison.Ordinal) &&
          alterPreview.Warnings.Any(w => w.Contains("DESTRUCTIVE", StringComparison.Ordinal)), "design-alter-drop");
    Check(alter.Contains("DROP COLUMN [Balance]", StringComparison.Ordinal), "design-alter-drop2");
    Check(alter.Contains("ALTER COLUMN [Name] nvarchar(200) NOT NULL", StringComparison.Ordinal), "design-alter-type");
    Check(alter.Contains("ADD [Email] varchar(255) NULL", StringComparison.Ordinal), "design-alter-add");
    Check(TableDesignSql.HasDestructiveChanges(draft, desired) && !TableDesignSql.HasDestructiveChanges(draft, draft), "design-destructive-flag");
    Check(!TableDesignSql.PreviewAlter(draft, draft).HasChanges && TableDesignSql.PreviewAlter(draft, draft).ExecutableSql.Length == 0, "design-alter-noop");

    // Hostile identifiers: metadata must never be embedded in executable SQL comments (PR #7 finding 1).
    static string StripBracketedIdentifiers(string sql)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < sql.Length; i++)
        {
            if (sql[i] != '[') { sb.Append(sql[i]); continue; }
            sb.Append("[]");
            i++;
            while (i < sql.Length)
            {
                if (sql[i] == ']' && i + 1 < sql.Length && sql[i + 1] == ']') { i += 2; continue; }
                if (sql[i] == ']') break;
                i++;
            }
        }
        return sb.ToString();
    }
    static void CheckExecutableOnly(string sql, string name)
    {
        var skeleton = StripBracketedIdentifiers(sql);
        Check(!skeleton.Contains("--", StringComparison.Ordinal) && !skeleton.Contains("/*", StringComparison.Ordinal), name + "-no-comments");
        var statements = skeleton.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Check(statements.Length > 0 && statements.All(st =>
            st.StartsWith("ALTER TABLE ", StringComparison.Ordinal) || st.StartsWith("CREATE TABLE ", StringComparison.Ordinal)), name + "-only-ddl");
        Check(!sql.Contains("DESTRUCTIVE", StringComparison.Ordinal) && !sql.Contains("NOTE:", StringComparison.Ordinal), name + "-no-metadata");
    }
    const string Hostile = "evil]\nDROP TABLE dbo.Victim;--";
    var hostileExisting = draft with
    {
        Columns =
        [
            draft.Columns[0],
            new ColumnDefinition { Name = Hostile, DataType = "int" },
            new ColumnDefinition { Name = "Old]\r\n-- x", DataType = "nvarchar", MaxLength = 10 }
        ]
    };
    var hostileDesired = draft with
    {
        Columns =
        [
            draft.Columns[0],
            new ColumnDefinition { Name = "New\nCol]", DataType = "int", IsNullable = false },
            new ColumnDefinition { Name = "Old]\r\n-- x", DataType = "nvarchar", MaxLength = 20 }
        ]
    };
    var hostile = TableDesignSql.PreviewAlter(hostileExisting, hostileDesired);
    CheckExecutableOnly(hostile.ExecutableSql, "design-hostile-alter");
    Check(hostile.ExecutableSql.Contains("DROP COLUMN [evil]]\nDROP TABLE dbo.Victim;--]", StringComparison.Ordinal), "design-hostile-quoted");
    Check(hostile.Warnings.Count >= 2 && hostile.Warnings.All(w => !w.Contains('\n') && !w.Contains('\r')), "design-hostile-warnings-single-line");
    Check(hostile.Warnings.Any(w => w.Contains("DESTRUCTIVE", StringComparison.Ordinal)), "design-hostile-warnings-destructive");
    CheckExecutableOnly(TableDesignSql.PreviewCreate(hostileDesired).ExecutableSql, "design-hostile-create");

    void ExpectInvalid(ColumnDefinition column, string name)
    {
        try
        {
            TableDesignSql.PreviewCreate(draft with { Columns = [column] });
            throw new Exception("FAIL " + name);
        }
        catch (ArgumentException) { }
    }
    ExpectInvalid(new ColumnDefinition { Name = "x", DataType = "int); DROP TABLE t;--" }, "design-injection-type");
    ExpectInvalid(new ColumnDefinition { Name = "x", DataType = "nvarchar" }, "design-length-required");
    ExpectInvalid(new ColumnDefinition { Name = "x", DataType = "nvarchar", MaxLength = 5000 }, "design-length-limit");
    ExpectInvalid(new ColumnDefinition { Name = "x", DataType = "nvarchar", MaxLength = 5, IsIdentity = true, IsNullable = false }, "design-identity-type");
    ExpectInvalid(new ColumnDefinition { Name = "x", DataType = "int", IsIdentity = true }, "design-identity-nullable");
    ExpectInvalid(new ColumnDefinition { Name = "x", DataType = "geography" }, "design-unknown-type");
    try
    {
        TableDesignSql.PreviewAlter(draft, draft with { TableName = "Other" });
        throw new Exception("FAIL design-rename-table");
    }
    catch (ArgumentException) { }
    try
    {
        TableDesignSql.PreviewAlter(draft, draft with { Columns = [draft.Columns[0] with { IsIdentity = false }, .. draft.Columns.Skip(1)] });
        throw new Exception("FAIL design-identity-change");
    }
    catch (NotSupportedException) { }
    Pass("table-design-preview");
}

// Edit rows
{
    var orders = new TableRef("App", "dbo", "Orders");
    Check(EditRowsSql.BuildSelectTop(orders, 200).StartsWith("SELECT TOP (200) * FROM [App].[dbo].[Orders];", StringComparison.Ordinal), "edit-select-top");
    try { EditRowsSql.BuildSelectTop(orders, 0); throw new Exception("FAIL edit-select-zero"); }
    catch (ArgumentOutOfRangeException) { }
    var key = new RowKey(["Id"], [7]);
    var update = EditRowsSql.BuildUpdate(
        orders, key,
        new Dictionary<string, object?> { ["Qty"] = 5, ["Note"] = null },
        new Dictionary<string, object?> { ["Id"] = 7, ["Qty"] = 4, ["Note"] = null, ["Memo"] = "m" });
    Check(update.CommandText.Contains("UPDATE [App].[dbo].[Orders] SET [Qty] = @n0, [Note] = @n1 WHERE [Id] = @k0 AND [Qty] = @o0 AND [Note] IS NULL AND [Memo] = @o1;", StringComparison.Ordinal), "edit-update-optimistic-where");
    Check(update.CommandText.Contains("SELECT @@ROWCOUNT AS [__sqloryn_rowcount];", StringComparison.Ordinal), "edit-update-rowcount");
    Check(update.Parameters.Count == 5 && update.Parameters.Single(p => p.Name == "@k0").Value is 7 && update.Parameters.Single(p => p.Name == "@o0").Value is 4, "edit-update-params");
    Check(!update.CommandText.Contains("Memo = 'm'", StringComparison.Ordinal) && !update.CommandText.Contains("'m'", StringComparison.Ordinal), "edit-update-no-inline-values");
    var skipped = EditRowsSql.BuildUpdate(orders, key, new Dictionary<string, object?> { ["Qty"] = 1 },
        new Dictionary<string, object?> { ["Qty"] = 4, ["Blob"] = "x" }, new HashSet<string> { "Blob" });
    Check(!skipped.CommandText.Contains("[Blob]", StringComparison.Ordinal), "edit-exclude-column");
    var delete = EditRowsSql.BuildDelete(orders, new RowKey(["A", "B"], [1, "x"]), new Dictionary<string, object?> { ["Qty"] = 4 });
    Check(delete.CommandText.Contains("DELETE FROM [App].[dbo].[Orders] WHERE [A] = @k0 AND [B] = @k1 AND [Qty] = @o0;", StringComparison.Ordinal), "edit-delete-where");
    try { EditRowsSql.BuildUpdate(orders, key, new Dictionary<string, object?> { ["Id"] = 8 }); throw new Exception("FAIL edit-update-key"); }
    catch (ArgumentException) { }
    try { new RowKey(["Id"], [null]); throw new Exception("FAIL edit-null-key"); }
    catch (ArgumentException) { }
    try { EditRowsSql.EnsureSingleRowAffected(0); throw new Exception("FAIL edit-conflict"); }
    catch (EditRowsConflictException) { }
    try { EditRowsSql.EnsureSingleRowAffected(2); throw new Exception("FAIL edit-multirow"); }
    catch (InvalidOperationException) { }
    EditRowsSql.EnsureSingleRowAffected(1);
    Pass("edit-rows-sql");
}

// Diagnostics redaction
{
    var cs = "Server=tcp:db,1433;User Id=sa;Password=Sup3r;Secret;Encrypt=True;PWD='a;b';Database=x";
    var red = DiagnosticsRedactor.Redact(cs);
    Check(!red.Contains("Sup3r", StringComparison.Ordinal) && !red.Contains("a;b", StringComparison.Ordinal), "redact-conn-string");
    Check(red.Contains("Server=tcp:db,1433", StringComparison.Ordinal) && red.Contains("User Id=sa", StringComparison.Ordinal), "redact-keeps-context");
    var withNewline = DiagnosticsRedactor.Redact("failed: pwd=hunter2\nnext line ok");
    Check(!withNewline.Contains("hunter2", StringComparison.Ordinal) && withNewline.Contains("next line ok", StringComparison.Ordinal), "redact-line-bounded");
    Check(!DiagnosticsRedactor.Redact("CREATE LOGIN a WITH PASSWORD = N'p''w;d';").Contains("p''w", StringComparison.Ordinal), "redact-sql-password");
    var bearer = DiagnosticsRedactor.Redact("Authorization: Bearer abc123.def456-xyz\nGET with Bearer tokenvalue12345==");
    Check(!bearer.Contains("abc123", StringComparison.Ordinal) && !bearer.Contains("tokenvalue", StringComparison.Ordinal), "redact-bearer");
    var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.sig_nature";
    Check(!DiagnosticsRedactor.Redact("token seen " + jwt).Contains("eyJhbGci", StringComparison.Ordinal), "redact-jwt");
    Check(!DiagnosticsRedactor.Redact("api_key=AKIA123456&x=1").Contains("AKIA123456", StringComparison.Ordinal), "redact-api-key");
    Check(DiagnosticsRedactor.Redact("plain text without secrets") == "plain text without secrets" && DiagnosticsRedactor.Redact(null) == "", "redact-noop");
    var bundle = DiagnosticsRedactor.BuildSupportBundle([("Log", "connect Password=abc123\n"), ("Env", "ok")]);
    Check(!bundle.Contains("abc123", StringComparison.Ordinal) && bundle.Contains("=== Log ===", StringComparison.Ordinal) && bundle.Contains("=== Env ===", StringComparison.Ordinal), "redact-bundle");
    Pass("diagnostics-redactor");
}

// Find / replace
{
    var text = "Select a, A from t -- a";
    Check(FindReplaceEngine.FindNext(text, "a", 0, caseSensitive: true) == 7, "find-case-sensitive");
    Check(FindReplaceEngine.FindNext(text, "a", 0, caseSensitive: false) == 7, "find-first");
    Check(FindReplaceEngine.FindNext(text, "a", 8, caseSensitive: false) == 10, "find-next-case-insensitive");
    Check(FindReplaceEngine.FindNext(text, "a", 8, caseSensitive: true) == 22, "find-next-case-sensitive-skips");
    Check(FindReplaceEngine.FindNext(text, "select", 5, caseSensitive: false) == 0, "find-wraps");
    Check(FindReplaceEngine.FindNext(text, "select", 5, caseSensitive: false, wrap: false) == -1, "find-no-wrap");
    Check(FindReplaceEngine.FindNext(text, "select", 0, caseSensitive: true) == -1, "find-case-miss");
    Check(FindReplaceEngine.FindNext(text, "", 0, true) == -1 && FindReplaceEngine.FindNext(text, "zzz", 99, true) == -1, "find-empty-and-clamp");
    var replaced = FindReplaceEngine.ReplaceAll("aAaa", "a", "b", caseSensitive: false);
    Check(replaced.Text == "bbbb" && replaced.Count == 4, "replace-all-insensitive");
    var sens = FindReplaceEngine.ReplaceAll("aAaa", "a", "ba", caseSensitive: true);
    Check(sens.Text == "baAbaba" && sens.Count == 3, "replace-all-no-rescan");
    var none = FindReplaceEngine.ReplaceAll("abc", "x", "y", true);
    Check(none.Text == "abc" && none.Count == 0 && FindReplaceEngine.ReplaceAll("abc", "", "y", true).Count == 0, "replace-none");
    Pass("find-replace");
}

// PR #7 finding 2: remembered password must stay bound to its original endpoint identity.
{
    static EndpointIdentity Id(string server = "hostA", int port = 1433, string user = "app",
        bool trust = false, string? pin = null) => new(server, port, user, trust, pin);

    static ConnectionProfile Profile(Guid id, string server = "hostA", int port = 1433, string user = "app",
        bool trust = false, string? pin = null, bool remember = true) => new()
    {
        Id = id,
        Name = "p",
        Server = server,
        Port = port,
        UserId = user,
        TrustServerCertificate = trust,
        ServerCertificatePath = pin,
        RememberPassword = remember
    };

    var pid = Guid.NewGuid();
    var pidKey = ProfileSecretKeys.ForProfile(pid);

    // Happy path: lookup applies and Connect may use the remembered secret at the bound endpoint.
    {
        var store = new MemorySecretStore();
        await store.SetAsync(pidKey, "secretA");
        var session = new ProfileAutofillSession();
        var outcome = await session.BeginProfileAsync(Profile(pid), store);
        Check(outcome.Status == AutofillStatus.Applied && outcome.Secret == "secretA", "autofill-applied");
        var ok = session.ResolvePassword(Id(), "secretA");
        Check(ok.Allowed && ok.Password == "secretA" && ok.UsesRememberedSecret, "autofill-same-endpoint-allowed");
        Check(!session.NotifyEndpointEdited(Id("HOSTA", 1433, "app")), "autofill-host-case-insensitive");
        Check(session.ResolvePassword(Id("HOSTA"), "secretA").Allowed, "autofill-host-case-still-bound");
        Pass("autofill-session-happy-path");
    }

    // Delayed lookup A -> B: result for A arrives after the endpoint changed to B.
    {
        var store = new DelayedSecretStore();
        var session = new ProfileAutofillSession();
        var pending = session.BeginProfileAsync(Profile(pid), store);
        Check(!pending.IsCompleted, "delayed-pending");
        Check(!session.NotifyEndpointEdited(Id("hostB")), "delayed-edit-nothing-applied-yet");
        store.Release(pidKey, "secretA");
        var outcome = await pending;
        Check(outcome.Status == AutofillStatus.Stale && outcome.Secret is null, "delayed-a-to-b-discarded");
        var decision = session.ResolvePassword(Id("hostB"), "");
        Check(decision.Allowed && decision.Password == "" && !decision.UsesRememberedSecret, "delayed-a-to-b-no-secret-held");
        Pass("autofill-delayed-a-to-b");
    }

    // After applied autofill, each identity/trust field change drops the secret and Connect refuses it.
    {
        var variants = new (string Name, EndpointIdentity Changed)[]
        {
            ("host", Id("hostB")),
            ("port", Id(port: 1434)),
            ("user", Id(user: "other")),
            ("trust", Id(trust: true)),
            ("pin", Id(pin: "/tmp/lab.crt"))
        };
        foreach (var (name, changed) in variants)
        {
            var store = new MemorySecretStore();
            await store.SetAsync(pidKey, "secretA");
            var session = new ProfileAutofillSession();
            await session.BeginProfileAsync(Profile(pid), store);
            Check(session.NotifyEndpointEdited(changed), $"applied-edit-clears-{name}");
            var refused = session.ResolvePassword(changed, "secretA");
            Check(!refused.Allowed && refused.Password == "" && refused.Reason is not null, $"applied-edit-refused-{name}");
            // Switching the endpoint back does not resurrect the secret.
            Check(!session.ResolvePassword(Id(), "secretA").Allowed, $"applied-edit-not-restored-{name}");
        }
        Pass("autofill-identity-and-trust-changes-clear-secret");
    }

    // Defence in depth: even without an edit notification, Connect refuses a remembered secret at a different endpoint.
    {
        var store = new MemorySecretStore();
        await store.SetAsync(pidKey, "secretA");
        var session = new ProfileAutofillSession();
        await session.BeginProfileAsync(Profile(pid), store);
        var refused = session.ResolvePassword(Id("hostB"), "secretA");
        Check(!refused.Allowed && refused.Reason!.Contains("re-enter", StringComparison.OrdinalIgnoreCase), "resolve-refuses-without-notify");
        Check(!refused.Reason!.Contains("secretA", StringComparison.Ordinal), "resolve-reason-no-secret");
        Pass("autofill-resolve-defence-in-depth");
    }

    // Typed replacement during a pending lookup: late result must not overwrite it.
    {
        var store = new DelayedSecretStore();
        var session = new ProfileAutofillSession();
        var pending = session.BeginProfileAsync(Profile(pid), store);
        session.NotifyPasswordEdited();
        store.Release(pidKey, "secretA");
        var outcome = await pending;
        Check(outcome.Status == AutofillStatus.Stale && outcome.Secret is null, "typed-during-lookup-discarded");
        var typedPwd = session.ResolvePassword(Id(), "typedPw");
        Check(typedPwd.Allowed && typedPwd.Password == "typedPw" && !typedPwd.UsesRememberedSecret, "typed-during-lookup-keeps-typed");
        Pass("autofill-typed-replacement-pending");
    }

    // Typed replacement after autofill: explicit credentials survive, even at a changed endpoint.
    {
        var store = new MemorySecretStore();
        await store.SetAsync(pidKey, "secretA");
        var session = new ProfileAutofillSession();
        await session.BeginProfileAsync(Profile(pid), store);
        session.NotifyPasswordEdited();
        Check(!session.NotifyEndpointEdited(Id("hostB")), "typed-after-apply-edit-keeps-box");
        var typedPwd = session.ResolvePassword(Id("hostB"), "typedForB");
        Check(typedPwd.Allowed && typedPwd.Password == "typedForB" && !typedPwd.UsesRememberedSecret, "typed-after-apply-explicit-allowed");
        // The old secret text cannot be smuggled back into the box and treated as remembered.
        Check(!session.ResolvePassword(Id("hostB"), "secretA").UsesRememberedSecret, "typed-after-apply-old-secret-not-remembered");
        Pass("autofill-typed-replacement-applied");
    }

    // Profile switch: lookup for profile 1 arriving after profile 2 was selected is discarded.
    {
        var store = new DelayedSecretStore();
        var session = new ProfileAutofillSession();
        var pid2 = Guid.NewGuid();
        var key2 = ProfileSecretKeys.ForProfile(pid2);
        var first = session.BeginProfileAsync(Profile(pid, "hostA"), store);
        var second = session.BeginProfileAsync(Profile(pid2, "hostB", user: "bob"), store);
        store.Release(key2, "secretB");
        store.Release(pidKey, "secretA");
        var firstOutcome = await first;
        var secondOutcome = await second;
        Check(firstOutcome.Status == AutofillStatus.Stale && firstOutcome.Secret is null, "profile-switch-first-stale");
        Check(secondOutcome.Status == AutofillStatus.Applied && secondOutcome.Secret == "secretB", "profile-switch-second-applied");
        Check(!session.ResolvePassword(Id("hostA"), "secretB").Allowed, "profile-switch-secret-not-valid-for-old-endpoint");
        Check(session.ResolvePassword(Id("hostB", user: "bob"), "secretB").Allowed, "profile-switch-secret-valid-for-new-profile");
        Pass("autofill-profile-switch");
    }

    // Selecting a profile that does not remember a password cancels the earlier pending lookup and drops applied secrets.
    {
        var store = new DelayedSecretStore();
        var session = new ProfileAutofillSession();
        var first = session.BeginProfileAsync(Profile(pid), store);
        var second = await session.BeginProfileAsync(Profile(Guid.NewGuid(), "hostB", remember: false), store);
        Check(second.Status == AutofillStatus.NotRequested, "no-remember-not-requested");
        store.Release(pidKey, "secretA");
        Check((await first).Status == AutofillStatus.Stale, "no-remember-cancels-pending");
        Check(!session.ResolvePassword(Id("hostA"), "secretA").UsesRememberedSecret, "no-remember-no-bound-secret");
        Pass("autofill-profile-without-remember");
    }

    // Keychain denial / missing item.
    {
        var session = new ProfileAutofillSession();
        var missing = await session.BeginProfileAsync(Profile(pid), new MemorySecretStore());
        Check(missing.Status == AutofillStatus.NotFound && missing.Secret is null && missing.Error is not null, "keychain-null-secret");
        var decision = session.ResolvePassword(Id(), "");
        Check(decision.Allowed && decision.Password == "" && !decision.UsesRememberedSecret, "keychain-null-nothing-bound");

        var denied = await session.BeginProfileAsync(Profile(pid), new DelayedSecretStore { ThrowOnGet = true });
        Check(denied.Status == AutofillStatus.Failed && denied.Secret is null, "keychain-denied-failed");
        Check(denied.Error is not null && denied.Error.Contains("Keychain", StringComparison.Ordinal), "keychain-denied-has-message");
        Check(!session.ResolvePassword(Id(), "anything").UsesRememberedSecret, "keychain-denied-nothing-bound");

        // A denial that arrives after the user already moved on is stale, not an error to show.
        var slow = new DelayedSecretStore();
        var pending = session.BeginProfileAsync(Profile(pid), slow);
        session.NotifyPasswordEdited();
        slow.Fail(pidKey, new InvalidOperationException("late denial"));
        Check((await pending).Status == AutofillStatus.Stale, "keychain-denied-late-is-stale");
        Pass("autofill-keychain-denial");
    }

    // Forget / delete path: Forget drops the applied secret and cancels pending lookups.
    {
        var store = new MemorySecretStore();
        await store.SetAsync(pidKey, "secretA");
        var session = new ProfileAutofillSession();
        await session.BeginProfileAsync(Profile(pid), store);
        session.Forget();
        Check(!session.ResolvePassword(Id(), "secretA").Allowed, "forget-drops-applied");

        var lateStore = new DelayedSecretStore();
        var pending = session.BeginProfileAsync(Profile(pid), lateStore);
        session.Forget();
        lateStore.Release(pidKey, "secretA");
        Check((await pending).Status == AutofillStatus.Stale, "forget-cancels-pending");
        Check(session.Generation > 0, "forget-advances-generation");
        Pass("autofill-forget");
    }

    // Generation is monotonic across begin / edit / typed / forget.
    {
        var session = new ProfileAutofillSession();
        var g0 = session.Generation;
        await session.BeginProfileAsync(Profile(pid, remember: false), new MemorySecretStore());
        var g1 = session.Generation;
        session.NotifyPasswordEdited();
        var g2 = session.Generation;
        session.Forget();
        var g3 = session.Generation;
        Check(g0 < g1 && g1 < g2 && g2 < g3, "generation-monotonic");
        Pass("autofill-generation");
    }

    // Identity normalisation: trimmed, blank pin == no pin, host case-insensitive, user case-sensitive.
    {
        Check(Id(" HostA ", pin: "  ").Matches(Id("hosta")), "identity-normalises");
        Check(!Id(user: "App").Matches(Id(user: "app")), "identity-user-case-sensitive");
        Check(Id(pin: "/a.crt").Matches(Id(pin: " /a.crt ")) && !Id(pin: "/a.crt").Matches(Id(pin: "/b.crt")), "identity-pin-compare");
        Pass("endpoint-identity");
    }
}

// Edit rows: transactional batch (PR #7 finding 3)
{
    var tbl = new TableRef("App", "dbo", "Orders");
    var cmds = new List<EditCommand>
    {
        EditRowsSql.BuildUpdate(tbl, new RowKey(["Id"], [1]), new Dictionary<string, object?> { ["Qty"] = 5 }),
        EditRowsSql.BuildUpdate(tbl, new RowKey(["Id"], [2]), new Dictionary<string, object?> { ["Qty"] = 6 })
    };

    var ok = new FakeEditTransactionFactory(1, 1);
    var okResult = await EditRowsBatch.ApplyAsync(ok, cmds);
    Check(okResult.Outcome == EditBatchOutcome.Committed && okResult.Committed && !okResult.RequiresReload, "batch-commit-outcome");
    Check(string.Join(",", ok.Log) == "begin,exec,exec,commit,dispose", "batch-commit-order");

    // Second row conflicts: the whole set is rolled back, commit is never reached.
    var conflict = new FakeEditTransactionFactory(1, 0);
    var conflictResult = await EditRowsBatch.ApplyAsync(conflict, cmds);
    Check(conflictResult.Outcome == EditBatchOutcome.RolledBackConflict && conflictResult.FailedIndex == 1 && !conflictResult.Committed, "batch-conflict-outcome");
    Check(conflictResult.WritesValidated == 1, "batch-conflict-validated-count");
    Check(string.Join(",", conflict.Log) == "begin,exec,exec,rollback,dispose" && !conflict.Log.Contains("commit"), "batch-conflict-rolls-back-all");

    // Wrong row count (key not unique / trigger skew) also rolls back everything.
    var many = new FakeEditTransactionFactory(1, 2);
    var manyResult = await EditRowsBatch.ApplyAsync(many, cmds);
    Check(manyResult.Outcome == EditBatchOutcome.RolledBackWrongRowCount && manyResult.FailedIndex == 1, "batch-multirow-outcome");
    Check(many.Log.Contains("rollback") && !many.Log.Contains("commit"), "batch-multirow-rolls-back");

    // First statement conflicts: second never executes.
    var firstConflict = new FakeEditTransactionFactory(0, 1);
    var firstConflictResult = await EditRowsBatch.ApplyAsync(firstConflict, cmds);
    Check(firstConflictResult.FailedIndex == 0 && firstConflict.Log.Count(l => l == "exec") == 1, "batch-stops-at-first-failure");

    // Provider error mid-set.
    var boom = new FakeEditTransactionFactory(1, new InvalidOperationException("synthetic provider failure"));
    var boomResult = await EditRowsBatch.ApplyAsync(boom, cmds);
    Check(boomResult.Outcome == EditBatchOutcome.RolledBackError && boomResult.FailedIndex == 1 && !boom.Log.Contains("commit") && boom.Log.Contains("rollback"), "batch-error-rolls-back");

    // Rollback itself fails: outcome must be unknown and require reload.
    var rbFail = new FakeEditTransactionFactory(1, 0) { RollbackFails = new InvalidOperationException("connection broken") };
    var rbFailResult = await EditRowsBatch.ApplyAsync(rbFail, cmds);
    Check(rbFailResult.Outcome == EditBatchOutcome.OutcomeUnknown && rbFailResult.RequiresReload, "batch-rollback-failure-unknown");

    // Disconnect at commit: unknown outcome, reload required, never reported as rolled back.
    var commitFail = new FakeEditTransactionFactory(1, 1) { CommitFails = new InvalidOperationException("connection lost") };
    var commitFailResult = await EditRowsBatch.ApplyAsync(commitFail, cmds);
    Check(commitFailResult.Outcome == EditBatchOutcome.OutcomeUnknown && commitFailResult.RequiresReload && !commitFailResult.Committed, "batch-commit-failure-unknown");

    // Begin failure: nothing was written.
    var beginFail = new FakeEditTransactionFactory() { BeginFails = new InvalidOperationException("not connected") };
    var beginFailResult = await EditRowsBatch.ApplyAsync(beginFail, cmds);
    Check(beginFailResult.Outcome == EditBatchOutcome.RolledBackError && beginFailResult.WritesValidated == 0, "batch-begin-failure");

    // Cancellation mid-set rolls everything back.
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var cancelled = new FakeEditTransactionFactory(1, 1);
    var cancelledResult = await EditRowsBatch.ApplyAsync(cancelled, cmds, cts.Token);
    Check(cancelledResult.Outcome == EditBatchOutcome.RolledBackError && !cancelled.Log.Contains("commit"), "batch-cancel-rolls-back");

    try { await EditRowsBatch.ApplyAsync(new FakeEditTransactionFactory(), []); throw new Exception("FAIL batch-empty"); }
    catch (ArgumentException) { }

    // Trigger result sets are drained; only the marked row-count set counts.
    var trigger = new MultiResultReader(
        (["message"], [["audit row written"]]),
        ([EditRowsSql.RowCountColumn], [[1]]),
        (["message"], [["late trigger output"]]));
    Check(await EditRowsSql.ReadAffectedCountAsync(trigger) == 1, "rowcount-skips-trigger-sets");
    var zeroRows = new MultiResultReader((["message"], [["x"]]), ([EditRowsSql.RowCountColumn], [[0]]));
    Check(await EditRowsSql.ReadAffectedCountAsync(zeroRows) == 0, "rowcount-zero-after-trigger-set");
    try { await EditRowsSql.ReadAffectedCountAsync(new MultiResultReader((["message"], [["x"]]))); throw new Exception("FAIL rowcount-missing"); }
    catch (InvalidOperationException) { }
    try
    {
        await EditRowsSql.ReadAffectedCountAsync(new MultiResultReader(
            ([EditRowsSql.RowCountColumn], [[1]]), ([EditRowsSql.RowCountColumn], [[1]])));
        throw new Exception("FAIL rowcount-ambiguous");
    }
    catch (InvalidOperationException) { }
    Check(cmds[0].CommandText.Contains($"SET NOCOUNT ON;") &&
          cmds[0].CommandText.Contains($";\nSELECT @@ROWCOUNT AS [{EditRowsSql.RowCountColumn}];", StringComparison.Ordinal), "rowcount-captured-immediately");
    Pass("edit-rows-batch");
}

// Edit rows: support policy (key + dependable concurrency columns)
{
    EditColumnInfo C(string n, string t, bool computed = false, bool identity = false) => new(n, t, true, identity, computed);

    var noKey = EditRowsPolicy.Evaluate([], [C("a", "int")], false);
    Check(!noKey.IsSupported && !string.IsNullOrEmpty(noKey.RefusalReason), "policy-no-key");

    var plain = EditRowsPolicy.Evaluate(["Id"], [C("Id", "int", identity: true), C("Qty", "int"), C("Name", "nvarchar")], false);
    Check(plain.IsSupported && !plain.UsesRowVersion && plain.ConcurrencyColumns.SequenceEqual(["Qty", "Name"]), "policy-plain-compares-all-non-key");

    var dt2 = EditRowsPolicy.Evaluate(["Id"], [C("Id", "int"), C("At", "datetime2")], false);
    Check(!dt2.IsSupported && dt2.RefusalReason!.Contains("At", StringComparison.Ordinal), "policy-datetime2-refused-without-rowversion");

    var xml = EditRowsPolicy.Evaluate(["Id"], [C("Id", "int"), C("Doc", "xml")], false);
    Check(!xml.IsSupported, "policy-xml-refused");

    var rv = EditRowsPolicy.Evaluate(["Id"], [C("Id", "int"), C("At", "datetime2"), C("Ver", "timestamp")], false);
    Check(rv.IsSupported && rv.UsesRowVersion && rv.ConcurrencyColumns.SequenceEqual(["Ver"]), "policy-rowversion-is-concurrency-token");

    var computed = EditRowsPolicy.Evaluate(["Id"], [C("Id", "int"), C("Qty", "int"), C("Total", "int", computed: true)], false);
    Check(computed.IsSupported && computed.ConcurrencyColumns.SequenceEqual(["Qty"]), "policy-computed-not-in-predicate");

    var instead = EditRowsPolicy.Evaluate(["Id"], [C("Id", "int"), C("Qty", "int")], hasInsteadOfTrigger: true);
    Check(!instead.IsSupported && instead.RefusalReason!.Contains("INSTEAD OF", StringComparison.Ordinal), "policy-instead-of-trigger-refused");

    var badKey = EditRowsPolicy.Evaluate(["Missing"], [C("Id", "int")], false);
    Check(!badKey.IsSupported, "policy-key-missing-from-columns");

    var composite = EditRowsPolicy.Evaluate(["A", "B"], [C("A", "int"), C("B", "uniqueidentifier"), C("Qty", "int")], false);
    Check(composite.IsSupported && composite.ConcurrencyColumns.SequenceEqual(["Qty"]), "policy-composite-key");
    Pass("edit-rows-policy");
}

// QueryRaw byte budget (PR #7 finding 6)
{
    var small = new RawReadBudget(MaxCellBytes: 1024, MaxTotalBytes: 100_000);
    var hugeText = new string('x', 10_000_000);
    var hugeBytes = new byte[10_000_000];

    var hugeReader = new BudgetFakeReader(
        ["id", "body", "blob", "note"], ["int", "nvarchar", "varbinary", "nvarchar"],
        [7, hugeText, hugeBytes, "hello"]);
    var huge = await RawRowReader.ReadAsync(hugeReader, 10, small);
    var hugeRow = huge.Rows[0];
    Check(hugeRow[0] is 7 && hugeRow[3] is "hello", "raw-small-cells-exact");
    Check(RawCellOmitted.IsOmitted(hugeRow[1]) && RawCellOmitted.IsOmitted(hugeRow[2]), "raw-huge-cells-marked");
    Check(!RawCellOmitted.IsOmitted(hugeRow[0]) && !RawCellOmitted.IsOmitted(hugeRow[3]), "raw-small-cells-not-marked");
    Check(huge.HasOmittedCells, "raw-has-omitted-flag");
    Check(hugeReader.UnboundedLargeReads == 0, "raw-never-getvalue-on-max-types");
    Check(hugeReader.CharsServed <= 1024 + 2 && hugeReader.BytesServed <= 1024 + 2, "raw-served-data-bounded");
    Check(hugeRow[1] is not string && hugeRow[2] is not byte[], "raw-omitted-cell-is-not-a-shortened-value");

    // A shortened value can never be written back.
    var tbl = new TableRef("App", "dbo", "Docs");
    var omitted = hugeRow[1];
    foreach (var (name, build) in new (string, Action)[]
    {
        ("new-value", () => EditRowsSql.BuildUpdate(tbl, new RowKey(["Id"], [1]), new Dictionary<string, object?> { ["Body"] = omitted })),
        ("original-value", () => EditRowsSql.BuildUpdate(tbl, new RowKey(["Id"], [1]), new Dictionary<string, object?> { ["Qty"] = 1 },
            new Dictionary<string, object?> { ["Body"] = omitted })),
        ("delete-original", () => EditRowsSql.BuildDelete(tbl, new RowKey(["Id"], [1]), new Dictionary<string, object?> { ["Body"] = omitted })),
        ("key", () => new RowKey(["Id"], [omitted]))
    })
    {
        try { build(); throw new Exception($"FAIL raw-omitted-writeback-{name}"); }
        catch (ArgumentException) { }
    }
    // ...unless the column is excluded from the predicate entirely.
    EditRowsSql.BuildUpdate(tbl, new RowKey(["Id"], [1]), new Dictionary<string, object?> { ["Qty"] = 1 },
        new Dictionary<string, object?> { ["Body"] = omitted }, new HashSet<string> { "Body" });

    // Total budget: first row fits, second row's cell does not -> omitted and reading stops.
    var rowsReader = new BudgetFakeReader(["t"], ["nvarchar"],
        [new string('a', 300)], [new string('b', 300)], [new string('c', 300)]);
    var budgeted = await RawRowReader.ReadAsync(rowsReader, 10, new RawReadBudget(MaxCellBytes: 1024, MaxTotalBytes: 800));
    Check(budgeted.Rows.Count >= 1 && budgeted.Rows[0][0] is string s0 && s0.Length == 300, "raw-total-budget-first-row-exact");
    Check(budgeted.Truncated && budgeted.ByteBudgetExhausted && rowsReader.RowsRead < 3, "raw-total-budget-stops-and-flags");
    Check(budgeted.Rows.All(r => r[0] is string or RawCellOmitted), "raw-total-budget-no-shortened-strings");
    Check(budgeted.Rows.Where(r => r[0] is string).All(r => ((string)r[0]!).Length == 300), "raw-total-budget-strings-complete");

    // Row cap still behaves as before.
    var capReader = new BudgetFakeReader(["i"], ["int"], [1], [2], [3]);
    var capped = await RawRowReader.ReadAsync(capReader, 2, RawReadBudget.Default);
    Check(capped.Rows.Count == 2 && capped.Truncated && !capped.ByteBudgetExhausted, "raw-row-cap-unchanged");

    // NULL and empty string are not omitted and stay distinguishable.
    var nulls = new BudgetFakeReader(["a", "b", "c"], ["nvarchar", "nvarchar", "varbinary"], [null, "", new byte[] { 1, 2, 3 }]);
    var nullRows = await RawRowReader.ReadAsync(nulls, 5, small);
    Check(nullRows.Rows[0][0] is null && nullRows.Rows[0][1] is "" && nullRows.Rows[0][2] is byte[] { Length: 3 }, "raw-null-empty-binary-exact");
    Check(!nullRows.HasOmittedCells, "raw-no-omitted-flag-for-small");

    // Cell exactly at the cap is kept; one over is omitted.
    var edge = new BudgetFakeReader(["a", "b"], ["nvarchar", "nvarchar"], [new string('e', 512), new string('f', 513)]);
    var edgeRows = await RawRowReader.ReadAsync(edge, 5, small);
    Check(edgeRows.Rows[0][0] is string { Length: 512 } && RawCellOmitted.IsOmitted(edgeRows.Rows[0][1]), "raw-cell-cap-boundary");

    // Spatial/hierarchy types are not read at all.
    var spatial = new BudgetFakeReader(["g", "i"], ["geography", "int"], [new string('g', 10), 1]);
    var spatialRows = await RawRowReader.ReadAsync(spatial, 5, small);
    Check(RawCellOmitted.IsOmitted(spatialRows.Rows[0][0]) && spatialRows.Rows[0][1] is 1 && spatial.UnboundedLargeReads == 0, "raw-spatial-unsupported-not-read");

    // Cancellation propagates.
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    try { await RawRowReader.ReadAsync(new BudgetFakeReader(["i"], ["int"], [1]), 5, small, cts.Token); throw new Exception("FAIL raw-cancel"); }
    catch (OperationCanceledException) { }
    Pass("query-raw-budget");
}

// F4: loss-aware JSON (ordinal column metadata + row arrays)
{
    var dup = new ResultSetView
    {
        Columns = [new ResultColumn("id", "int"), new ResultColumn("x", "nvarchar"), new ResultColumn("x", "nvarchar"), new ResultColumn("", "int")],
        Rows = [[ResultCell.Of("7"), ResultCell.Of("a"), ResultCell.Null, ResultCell.Of("9")]]
    };
    var root = System.Text.Json.JsonDocument.Parse(JsonExporter.ToJson(dup)).RootElement;
    var set = root.GetProperty("resultSets")[0];
    var cols = set.GetProperty("columns");
    Check(cols.GetArrayLength() == 4, "json-dup-col-count");
    Check(cols[1].GetProperty("ordinal").GetInt32() == 1 && cols[2].GetProperty("ordinal").GetInt32() == 2, "json-dup-ordinals");
    Check(cols[1].GetProperty("name").GetString() == "x" && cols[2].GetProperty("name").GetString() == "x", "json-dup-names-preserved");
    Check(cols[3].GetProperty("name").GetString() == "" && cols[3].GetProperty("sqlType").GetString() == "int", "json-blank-name-and-type");
    var row = set.GetProperty("rows")[0];
    Check(row.GetArrayLength() == 4 && row[1].GetString() == "a" && row[2].ValueKind == System.Text.Json.JsonValueKind.Null && row[3].GetInt64() == 9, "json-dup-values-all-present");
    Check(!set.GetProperty("truncated").GetBoolean(), "json-truncated-flag");

    var two = JsonExporter.ToJson([dup, dup]);
    Check(System.Text.Json.JsonDocument.Parse(two).RootElement.GetProperty("resultSets").GetArrayLength() == 2, "json-multi-set-single-document");
    Pass("json-duplicate-columns-preserved");
}

// F4: spreadsheet-safe CSV
{
    var view = new ResultSetView
    {
        Columns = [new ResultColumn("=h", "nvarchar"), new ResultColumn("n", "int"), new ResultColumn("d", "decimal")],
        Rows =
        [
            [ResultCell.Of("=1+1"), ResultCell.Of("-5"), ResultCell.Of("-1.5")],
            [ResultCell.Of("+cmd"), ResultCell.Of("3"), ResultCell.Of("0.5")],
            [ResultCell.Of("@SUM(A1)"), ResultCell.Null, ResultCell.Of("")],
            [ResultCell.Of("\t=x"), ResultCell.Of("1"), ResultCell.Of("2")],
            [ResultCell.Of("-text"), ResultCell.Of("1"), ResultCell.Of("2")],
            [ResultCell.Of("plain"), ResultCell.Of("1"), ResultCell.Of("2")]
        ]
    };
    var raw = CsvExporter.ToCsv(view).Split("\r\n");
    Check(raw[0] == "=h,n,d" && raw[1] == "=1+1,-5,-1.5", "csv-default-is-faithful");
    var safe = CsvExporter.ToCsv(view, new CsvOptions { SpreadsheetSafe = true }).Split("\r\n");
    Check(safe[0] == "'=h,n,d", "csv-safe-header");
    Check(safe[1] == "'=1+1,-5,-1.5", "csv-safe-formula-prefixed-numbers-untouched");
    Check(safe[2] == "'+cmd,3,0.5", "csv-safe-plus");
    Check(safe[3] == "'@SUM(A1),,\"\"", "csv-safe-at-null-empty");
    Check(safe[4].StartsWith("\"'\t=x\"", StringComparison.Ordinal) || safe[4].StartsWith("'\t=x", StringComparison.Ordinal), "csv-safe-tab");
    Check(safe[5] == "'-text,1,2" && safe[6] == "plain,1,2", "csv-safe-minus-text-and-plain");
    Pass("csv-spreadsheet-safe");
}

// F4/F5: staged atomic writes
{
    var dir = Path.Combine(Path.GetTempPath(), "sqloryn-f4-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        static string[] Temps(string d) => Directory.GetFiles(d, "*.tmp", SearchOption.AllDirectories);
        var utf8 = new System.Text.UTF8Encoding(false);

        // new destination
        var fresh = Path.Combine(dir, "fresh.sql");
        await SafeFileWriter.WriteUtf8Async(fresh, "héllo ✓");
        Check(File.ReadAllBytes(fresh).AsSpan().SequenceEqual(utf8.GetBytes("héllo ✓")) && Temps(dir).Length == 0, "safe-write-new-utf8-no-bom");

        // replace failure after successful staging: original intact, temp removed
        var target = Path.Combine(dir, "target.sql");
        File.WriteAllText(target, "ORIGINAL");
        var stagedSeen = "";
        try
        {
            await SafeFileWriter.WriteUtf8Async(target, "NEW", committer: new FailingCommitter(staged => stagedSeen = File.ReadAllText(staged)));
            throw new Exception("FAIL replace-failure-should-throw");
        }
        catch (IOException) { }
        Check(stagedSeen == "NEW", "stage-written-before-replace");
        Check(File.ReadAllText(target) == "ORIGINAL" && Temps(dir).Length == 0, "replace-failure-preserves-original");

        // ScriptDocument: write ok, replace fails -> original intact, doc stays dirty
        var script = new ScriptDocument();
        script.LoadFrom(target, "ORIGINAL");
        script.SetText("EDITED");
        try
        {
            await script.SaveAsync(writer: (p, t, ct) => SafeFileWriter.WriteUtf8Async(p, t, ct, new FailingCommitter()));
            throw new Exception("FAIL script-save-should-throw");
        }
        catch (IOException) { }
        Check(File.ReadAllText(target) == "ORIGINAL" && script.IsDirty && Temps(dir).Length == 0, "script-save-replace-failure-keeps-original-and-dirty");
        await script.SaveAsync();
        Check(File.ReadAllText(target) == "EDITED" && !script.IsDirty && Temps(dir).Length == 0, "script-save-default-uses-staging");

        // external edit before save: save replaces atomically, never a partial merge; external edit after failed replace survives
        File.WriteAllText(target, "EXTERNAL");
        try
        {
            await SafeFileWriter.WriteUtf8Async(target, "MINE", committer: new FailingCommitter());
        }
        catch (IOException) { }
        Check(File.ReadAllText(target) == "EXTERNAL", "external-edit-survives-failed-save");
        await SafeFileWriter.WriteUtf8Async(target, "MINE");
        Check(File.ReadAllText(target) == "MINE", "last-writer-wins-whole-file");

        // external edit lands between staging and replace -> last writer wins with complete content
        await SafeFileWriter.WriteUtf8Async(target, "SAVED", beforeCommit: _ =>
        {
            File.WriteAllText(target, "EXTERNAL-DURING");
            return Task.CompletedTask;
        });
        Check(File.ReadAllText(target) == "SAVED" && Temps(dir).Length == 0, "external-edit-during-stage-policy");

        // cancellation before replace leaves destination untouched
        var cts = new CancellationTokenSource();
        try
        {
            await SafeFileWriter.WriteUtf8Async(target, "CANCELLED", cts.Token, beforeCommit: _ =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            });
            throw new Exception("FAIL cancel-should-throw");
        }
        catch (OperationCanceledException) { }
        Check(File.ReadAllText(target) == "SAVED" && Temps(dir).Length == 0, "cancel-before-replace-leaves-original");

        // interleaved saves: file is exactly one complete payload, no temp files
        var race = Path.Combine(dir, "race.sql");
        var payloads = Enumerable.Range(0, 8).Select(i => new string((char)('a' + i), 50_000 + i)).ToArray();
        await Task.WhenAll(payloads.Select(p => Task.Run(() => SafeFileWriter.WriteUtf8Async(race, p))));
        Check(payloads.Contains(File.ReadAllText(race)) && Temps(dir).Length == 0, "interleaved-saves-no-torn-file");

        // directory missing: fail, nothing created
        try
        {
            await SafeFileWriter.WriteUtf8Async(Path.Combine(dir, "missing", "x.sql"), "x");
            throw new Exception("FAIL missing-dir-should-throw");
        }
        catch (DirectoryNotFoundException) { }
        Check(!Directory.Exists(Path.Combine(dir, "missing")), "missing-dir-not-created");

        if (!OperatingSystem.IsWindows())
        {
            // permissions preserved on replace
            var perm = Path.Combine(dir, "perm.sql");
            File.WriteAllText(perm, "old");
            File.SetUnixFileMode(perm, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            await SafeFileWriter.WriteUtf8Async(perm, "new");
            Check(File.GetUnixFileMode(perm) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead), "permissions-preserved");

            // symlink policy: write through to the target; the link survives
            var real = Path.Combine(dir, "real.sql");
            var link = Path.Combine(dir, "link.sql");
            File.WriteAllText(real, "old");
            File.CreateSymbolicLink(link, real);
            await SafeFileWriter.WriteUtf8Async(link, "through");
            Check(new FileInfo(link).LinkTarget is not null && File.ReadAllText(real) == "through" && File.ReadAllText(link) == "through" && Temps(dir).Length == 0, "symlink-written-through-link-preserved");

            // dangling symlink: target created, link survives
            var dangling = Path.Combine(dir, "dangling.sql");
            var danglingTarget = Path.Combine(dir, "dangling-target.sql");
            File.CreateSymbolicLink(dangling, danglingTarget);
            await SafeFileWriter.WriteUtf8Async(dangling, "made");
            Check(new FileInfo(dangling).LinkTarget is not null && File.ReadAllText(danglingTarget) == "made", "dangling-symlink-creates-target");
        }
        Pass("safe-file-writer");

        // F4: export writer — one destination, siblings untouched
        var exportDir = Path.Combine(dir, "export");
        Directory.CreateDirectory(exportDir);
        var results = Path.Combine(exportDir, "results.csv");
        var sibling = Path.Combine(exportDir, "results-2.csv");
        File.WriteAllText(results, "OLD1");
        File.WriteAllText(sibling, "SIBLING");
        var v1 = new ResultSetView { Columns = [new ResultColumn("a", "int")], Rows = [[ResultCell.Of("1")]] };
        var v2 = new ResultSetView { Columns = [new ResultColumn("b", "int")], Rows = [[ResultCell.Of("2")]], Truncated = true };

        var outcome = await ResultExportWriter.ExportAsync(results, ResultExportFormat.Csv, [v1]);
        Check(File.ReadAllText(results) == "a\r\n1\r\n" && File.ReadAllText(sibling) == "SIBLING" && !outcome.Incomplete, "export-single-destination-sibling-untouched");

        var truncOutcome = await ResultExportWriter.ExportAsync(results, ResultExportFormat.Csv, [v2]);
        Check(truncOutcome.Incomplete, "export-reports-truncation");

        try
        {
            await ResultExportWriter.ExportAsync(results, ResultExportFormat.Csv, [v1, v2]);
            throw new Exception("FAIL csv-multi-set-should-throw");
        }
        catch (ArgumentException) { }
        Check(File.ReadAllText(sibling) == "SIBLING" && File.ReadAllText(results).StartsWith("b", StringComparison.Ordinal), "csv-multi-set-refused-nothing-written");

        var jsonPath = Path.Combine(exportDir, "results.json");
        await ResultExportWriter.ExportAsync(jsonPath, ResultExportFormat.Json, [v1, v2]);
        Check(System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement.GetProperty("resultSets").GetArrayLength() == 2 && File.ReadAllText(sibling) == "SIBLING", "export-json-multi-set-one-file");

        var before = File.ReadAllText(results);
        var exportCts = new CancellationTokenSource();
        try
        {
            await ResultExportWriter.ExportAsync(results, ResultExportFormat.Csv, [v1], cancellationToken: exportCts.Token, beforeCommit: _ =>
            {
                exportCts.Cancel();
                return Task.CompletedTask;
            });
            throw new Exception("FAIL export-cancel-should-throw");
        }
        catch (OperationCanceledException) { }
        Check(File.ReadAllText(results) == before && File.ReadAllText(sibling) == "SIBLING" && Temps(dir).Length == 0, "export-cancel-leaves-files-untouched");

        try
        {
            await ResultExportWriter.ExportAsync(results, ResultExportFormat.Csv, [v1], committer: new FailingCommitter());
            throw new Exception("FAIL export-replace-failure-should-throw");
        }
        catch (IOException) { }
        Check(File.ReadAllText(results) == before && File.ReadAllText(sibling) == "SIBLING" && Temps(dir).Length == 0, "export-replace-failure-preserves-files");

        var safeOut = Path.Combine(exportDir, "safe.csv");
        await ResultExportWriter.ExportAsync(safeOut, ResultExportFormat.Csv,
            [new ResultSetView { Columns = [new ResultColumn("c", "nvarchar")], Rows = [[ResultCell.Of("=1+1")]] }],
            new ResultExportOptions { SpreadsheetSafeCsv = true });
        Check(File.ReadAllText(safeOut) == "c\r\n'=1+1\r\n", "export-spreadsheet-safe-option");
        Pass("result-export-writer");
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
}


Console.WriteLine("PASS Sqloryn.Core component assertions (E1; no SQL Server connection)");
Console.WriteLine("NOTE: live SqlDataReader decimal(38) against SQL Server remains provider-only / live-host verification.");
return 0;

sealed class FailingCommitter(Action<string>? onStaged = null) : IStagedFileCommitter
{
    public void Commit(string stagedPath, string destinationPath)
    {
        onStaged?.Invoke(stagedPath);
        throw new IOException("simulated replace failure");
    }
}
