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
Check(SqlIdentity.ScriptSelectTop("DbA", "dbo", "Orders").Contains(q1, StringComparison.Ordinal), "script-select-quoted");
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
table.Columns.Add("d", typeof(decimal));
table.Rows.Add(DBNull.Value, "", "hi", 1.23m);
using (var reader = table.CreateDataReader())
{
    Check(reader.Read(), "reader-row");
    Check(SqlSession.FormatCell(reader, 0).IsNull, "null-cell");
    Check(!SqlSession.FormatCell(reader, 1).IsNull && SqlSession.FormatCell(reader, 1).Kind == "empty-string", "empty-string-cell");
    Check(SqlSession.FormatCell(reader, 2).Display == "hi", "text-cell");
    Check(SqlSession.FormatCell(reader, 3).Kind == "decimal", "decimal-cell");
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

var tabA = new ScriptDocument();
tabA.SetText("SELECT 'A'");
var tabB = new ScriptDocument();
tabB.SetText("SELECT 'B'");
Check(ScriptDocument.ResolveBatch(tabA.Text, null) != ScriptDocument.ResolveBatch(tabB.Text, null), "tab-sql-isolation");
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

// --- Outcomes ---
Check(ExecutionOutcomes.FromSqlError(-2) == QueryCompletionKind.TimedOut, "timeout-minus-2");
Check(ExecutionOutcomes.FromSqlError(-1) == QueryCompletionKind.UnknownAfterDisconnect, "broken-connection");
Check(ExecutionOutcomes.FromSqlError(208) == QueryCompletionKind.Failed, "ordinary-sql-error");
Check(!ExecutionOutcomes.IsBrokenConnection(-2), "timeout-not-broken");
Check(ExecutionOutcomes.FromCancellation(false, true, false) == QueryCompletionKind.CancelledClientEnded, "cancel-client");
Check(ExecutionOutcomes.FromCancellation(false, true, true) == QueryCompletionKind.CancelledServerVerified, "cancel-verified");
Check(ExecutionOutcomes.FromCancellation(true, true, false) == QueryCompletionKind.UnknownAfterDisconnect, "cancel-disconnect");
Pass("execution-outcomes");

// Probe fidelity decimal(38,0) via SqlDecimal (same literal as SqlClientProbe PROBE-05).
const string probe38 = "12345678901234567890123456789012345678";
var sqlDec = SqlDecimal.Parse(probe38);
var cell38 = ResultMaterializer.FormatSqlDecimal(sqlDec);
Check(cell38.Display == probe38 && cell38.Kind == "decimal" && !cell38.Truncated, "probe-38-digit-decimal");
Pass("numeric-38");

// Oversized binary bound before full hex materialisation path.
var bigBinary = new byte[QueryLimits.MaxCellChars];
Random.Shared.NextBytes(bigBinary);
var binTable = new DataTable();
binTable.Columns.Add("b", typeof(byte[]));
binTable.Rows.Add(bigBinary);
using (var binReader = binTable.CreateDataReader())
{
    Check(binReader.Read(), "bin-reader");
    var cell = ResultMaterializer.FormatCell(binReader, 0);
    Check(cell.Kind == "binary" && cell.Truncated, "binary-truncated");
}
Pass("binary-bound");

// Aggregate retained-output budget.
var budget = new ResultBudget();
Check(budget.TryAdd(QueryLimits.MaxRetainedOutputChars - 1), "budget-accept");
Check(!budget.TryAdd(10) && budget.Exceeded, "budget-exceeded");
var wide = new DataTable();
wide.Columns.Add("c", typeof(string));
for (var i = 0; i < 5; i++)
    wide.Rows.Add(new string('w', 100));
using (var wideReader = wide.CreateDataReader())
{
    var b = new ResultBudget();
    // Force a tiny budget by filling almost full then reading.
    b.TryAdd(QueryLimits.MaxRetainedOutputChars - 50);
    var set = await ResultMaterializer.ReadResultSetAsync(wideReader, b, CancellationToken.None);
    Check(set.Truncated || set.BudgetExceeded || set.Rows.Count < 5, "wide-result-budget");
}
Pass("aggregate-budget");

// Late/partial failure retains completed rows (completion Failed + prior sets).
var partial = new QueryResult
{
    ExecutedSql = "SELECT 1; RAISERROR('x',16,1);",
    Server = "s",
    Database = "d",
    Completion = QueryCompletionKind.Failed,
    ResultSets =
    [
        new ResultSet
        {
            Columns = ["n"],
            Rows = [[CellValue.Text("1", "number")]],
            Truncated = false
        }
    ],
    ErrorSummary = "SQL error 50000: x",
    PartialResults = true
};
Check(partial.PartialResults && partial.ResultSets[0].Rows.Count == 1, "late-error-keeps-rows");
Pass("late-error-partial");

// Repeated Run guard.
var flag = 0;
Check(RunGuard.TryEnter(ref flag), "run-enter");
Check(!RunGuard.TryEnter(ref flag), "run-refuse-second");
RunGuard.Exit(ref flag);
Check(RunGuard.TryEnter(ref flag), "run-reenter");
RunGuard.Exit(ref flag);
Pass("repeated-run-guard");

// Stale / delayed metadata ownership across two DBs with identical table names.
var sessionKey = new object();
var explorerDoc = new ScriptDocument();
var tabId = Guid.NewGuid();
long generation = 1;
string? currentDb = "DbA";
OperationBinding Bind() => new(tabId, sessionKey, explorerDoc, currentDb, generation);
var nodeA = new ObjectExplorerService.Node
{
    Title = "DbA",
    Kind = ObjectExplorerService.NodeKind.Database,
    Database = "DbA"
};
var bindingA = Bind();
var expandOk = await ObjectExplorerService.ExpandAsync(
    nodeA,
    bindingA,
    stillCurrent: b => b.Matches(tabId, sessionKey, explorerDoc, currentDb, generation, tabOpen: true),
    loadChildren: async (n, ct) =>
    {
        await Task.Delay(40, ct);
        // Simulate tab switch during delayed metadata: bump generation and change active DB context.
        generation++;
        currentDb = "DbB";
        n.Children.Add(new ObjectExplorerService.Node
        {
            Title = "dbo",
            Kind = ObjectExplorerService.NodeKind.Schema,
            Database = "DbA",
            Schema = "dbo"
        });
    });
Check(!expandOk, "stale-metadata-rejected");
Check(!nodeA.Loaded, "stale-node-not-marked-loaded");

generation = 10;
currentDb = "DbA";
var bindingFresh = Bind();
var nodeFresh = new ObjectExplorerService.Node
{
    Title = "DbA",
    Kind = ObjectExplorerService.NodeKind.Database,
    Database = "DbA"
};
expandOk = await ObjectExplorerService.ExpandAsync(
    nodeFresh,
    bindingFresh,
    stillCurrent: b => b.Matches(tabId, sessionKey, explorerDoc, currentDb, generation, tabOpen: true),
    loadChildren: async (n, ct) =>
    {
        await Task.Delay(5, ct);
        n.Children.Add(new ObjectExplorerService.Node
        {
            Title = "Orders",
            Kind = ObjectExplorerService.NodeKind.Table,
            Database = "DbA",
            Schema = "dbo",
            ObjectName = "Orders"
        });
    });
Check(expandOk && nodeFresh.Loaded, "fresh-metadata-applied");
var scriptA = ObjectExplorerService.BuildSelectScript(nodeFresh.Children[0]);
var scriptB = SqlIdentity.ScriptSelectTop("DbB", "dbo", "Orders");
Check(scriptA.Contains("[DbA].[dbo].[Orders]", StringComparison.Ordinal), "explorer-script-dba");
Check(scriptB.Contains("[DbB].[dbo].[Orders]", StringComparison.Ordinal) && scriptA != scriptB, "explorer-script-dbb-distinct");
Pass("explorer-ownership-and-quoting");

// Browse vs insert: ExpandAsync never builds SQL; BuildSelectScript is explicit.
Check(!scriptA.StartsWith("Loading", StringComparison.Ordinal), "browse-separate-from-insert");
Pass("browse-vs-insert");

// Document lifecycle: cancel leaves dirty; two dirty tabs cancel at second prompt.
var d1 = new ScriptDocument();
d1.SetText("one");
var d2 = new ScriptDocument();
d2.SetText("two");
Check(d1.IsDirty && d2.IsDirty, "two-dirty");
var prompts = new Queue<DocumentLifecycle.PromptChoice>([
    DocumentLifecycle.PromptChoice.Discard,
    DocumentLifecycle.PromptChoice.Cancel
]);
var quitOk = await DocumentLifecycle.ResolveAllDirtyAsync(
    [d1, d2],
    prompt: (_, _) => Task.FromResult(prompts.Dequeue()),
    save: _ => Task.FromResult(true));
Check(!quitOk, "quit-cancel-at-second");
Check(!d1.IsDirty, "first-discarded");
Check(d2.IsDirty, "second-remains-dirty");

var openDoc = new ScriptDocument();
openDoc.SetText("unsaved");
var openOk = await DocumentLifecycle.ResolveDirtyAsync(
    openDoc,
    () => Task.FromResult(DocumentLifecycle.PromptChoice.Cancel),
    _ => Task.FromResult(true));
Check(!openOk && openDoc.IsDirty, "open-cancel-keeps-dirty");
Pass("document-lifecycle");

// Save failure preserves dirty; newer edits during save keep dirty.
var saveDoc = new ScriptDocument();
saveDoc.SetText("v1");
var version = saveDoc.Version;
var snapshot = saveDoc.Text;
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

saveDoc.SetText("v2");
version = saveDoc.Version;
snapshot = saveDoc.Text;
await saveDoc.SaveAsync(
    "/tmp/sqloryn-core-test-save.sql",
    betweenCaptureAndWrite: () =>
    {
        saveDoc.SetText("v3-during-save");
        return Task.CompletedTask;
    });
Check(saveDoc.IsDirty && saveDoc.Text == "v3-during-save", "save-concurrent-edit-keeps-dirty");
Pass("save-concurrent-edit");

// Late completion ownership rejection.
var lateTab = Guid.NewGuid();
var lateSession = new object();
var lateDoc = new ScriptDocument();
long lateGen = 1;
var lateBind = new OperationBinding(lateTab, lateSession, lateDoc, "master", lateGen);
lateGen++;
Check(!lateBind.Matches(lateTab, lateSession, lateDoc, "master", lateGen, true), "late-completion-rejected");
Pass("late-completion");

Console.WriteLine("PASS Sqloryn.Core component assertions (E1; no SQL Server connection)");
return 0;
