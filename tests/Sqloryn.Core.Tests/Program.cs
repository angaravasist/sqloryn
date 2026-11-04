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

// --- S-2: date / money / smallmoney through the production typed path ---
{
    var typedS2 = new FakeSqlTypedCellSource(
        ["date", "money", "smallmoney", "date", "money", "smallmoney", "money", "smallmoney"],
        [new DateTime(2024, 2, 29, 13, 45, 0), new SqlMoney(-12.3456m), new SqlMoney(214748.3647m),
         null, null, null, new SqlMoney(5m), new SqlMoney(-0.0001m)]);
    var dateCell = ResultMaterializer.FormatSqlTypedCell(typedS2, 0);
    Check(dateCell.Display == "2024-02-29" && dateCell.Kind == "date", "s2-date-iso-kind");
    var moneyCell = ResultMaterializer.FormatSqlTypedCell(typedS2, 1);
    Check(moneyCell.Display == "-12.3456" && moneyCell.Kind == "money", "s2-money-negative-exact");
    var smallCell = ResultMaterializer.FormatSqlTypedCell(typedS2, 2);
    Check(smallCell.Display == "214748.3647" && smallCell.Kind == "smallmoney", "s2-smallmoney-exact");
    Check(ResultMaterializer.FormatSqlTypedCell(typedS2, 3).IsNull, "s2-null-date");
    Check(ResultMaterializer.FormatSqlTypedCell(typedS2, 4).IsNull, "s2-null-money");
    Check(ResultMaterializer.FormatSqlTypedCell(typedS2, 5).IsNull, "s2-null-smallmoney");
    Check(ResultMaterializer.FormatSqlTypedCell(typedS2, 6).Display == "5.0000", "s2-money-fixed-scale");
    Check(ResultMaterializer.FormatSqlTypedCell(typedS2, 7).Display == "-0.0001", "s2-smallmoney-negative-fraction");

    var prevCulture = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        Check(ResultMaterializer.FormatSqlTypedCell(typedS2, 1).Display == "-12.3456", "s2-money-culture-invariant");
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = prevCulture; }

    var s2Set = new ResultSet
    {
        Columns = ["d", "m", "sm", "dn", "mn"],
        SqlTypes = ["date", "money", "smallmoney", "date", "money"],
        Rows =
        [[
            ResultMaterializer.FormatSqlTypedCell(typedS2, 0),
            ResultMaterializer.FormatSqlTypedCell(typedS2, 1),
            ResultMaterializer.FormatSqlTypedCell(typedS2, 7),
            ResultMaterializer.FormatSqlTypedCell(typedS2, 3),
            ResultMaterializer.FormatSqlTypedCell(typedS2, 4)
        ]]
    };
    var s2View = ResultSetView.FromResultSet(s2Set);
    var plainCsv = CsvExporter.ToCsv(s2View);
    Check(plainCsv.Split("\r\n")[1] == "2024-02-29,-12.3456,-0.0001,,", "s2-csv-plain");
    var safeCsv = CsvExporter.ToCsv(s2View, new CsvOptions { SpreadsheetSafe = true });
    Check(safeCsv.Split("\r\n")[1] == "2024-02-29,-12.3456,-0.0001,,", "s2-csv-spreadsheet-safe-money-unprefixed");
    var s2Json = System.Text.Json.JsonDocument.Parse(JsonExporter.ToJson(s2View)).RootElement
        .GetProperty("resultSets")[0].GetProperty("rows")[0];
    Check(s2Json[0].ValueKind == System.Text.Json.JsonValueKind.String && s2Json[0].GetString() == "2024-02-29", "s2-json-date-string");
    Check(s2Json[1].ValueKind == System.Text.Json.JsonValueKind.String && s2Json[1].GetString() == "-12.3456", "s2-json-money-string");
    Check(s2Json[2].ValueKind == System.Text.Json.JsonValueKind.String && s2Json[2].GetString() == "-0.0001", "s2-json-smallmoney-string");
    Check(s2Json[3].ValueKind == System.Text.Json.JsonValueKind.Null && s2Json[4].ValueKind == System.Text.Json.JsonValueKind.Null, "s2-json-null");

    // Kind-only (no provider SqlTypes): money/date kinds must still export spreadsheet-safe.
    var kindOnly = ResultSetView.FromResultSet(new ResultSet
    {
        Columns = ["m", "d"],
        Rows = [[CellValue.Text("-12.3456", "money"), CellValue.Text("2024-02-29", "date")]]
    });
    Check(kindOnly.Columns[0].SqlType == "money" && kindOnly.Columns[1].SqlType == "date", "s2-kind-infers-money-date");
    Check(CsvExporter.ToCsv(kindOnly, new CsvOptions { SpreadsheetSafe = true }).Contains("-12.3456", StringComparison.Ordinal)
          && !CsvExporter.ToCsv(kindOnly, new CsvOptions { SpreadsheetSafe = true }).Contains("'-12.3456", StringComparison.Ordinal), "s2-kind-money-safe-csv");

    // ReadResultSetAsync with typed fixture reader (fallback path for non-SqlDataReader).
    using var s2Reader = new TypedValueReader(
        ["d", "m", "sm"],
        ["date", "money", "smallmoney"],
        [typeof(DateTime), typeof(SqlMoney), typeof(SqlMoney)],
        [
            [new DateTime(2024, 2, 29, 23, 59, 59), new SqlMoney(-12.3456m), new SqlMoney(7.5m)],
            [null, null, null]
        ]);
    var s2Outcome = await ResultMaterializer.ReadResultSetAsync(s2Reader, new ResultBudget(), CancellationToken.None);
    Check(s2Outcome.RowFailure is null && s2Outcome.Set.Rows.Count == 2, "s2-read-resultset-no-failure");
    var r0Cells = s2Outcome.Set.Rows[0];
    Check(r0Cells[0].Display == "2024-02-29" && r0Cells[0].Kind == "date", "s2-read-resultset-date");
    Check(r0Cells[1].Display == "-12.3456" && r0Cells[1].Kind == "money", "s2-read-resultset-money");
    Check(r0Cells[2].Display == "7.5000" && r0Cells[2].Kind == "smallmoney", "s2-read-resultset-smallmoney");
    Check(s2Outcome.Set.Rows[1].All(c => c.IsNull), "s2-read-resultset-nulls");
    var s2ReadView = ResultSetView.FromResultSet(s2Outcome.Set);
    Check(s2ReadView.Columns[1].SqlType == "money", "s2-read-resultset-sqltype");
    Pass("s2-date-money-types");
}

// --- WS4-T1: time + uniqueidentifier through the production typed path ---
{
    var guidA = Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF");
    var typedT = new FakeSqlTypedCellSource(
        ["time", "time", "time", "uniqueidentifier", "uniqueidentifier", "time", "uniqueidentifier"],
        [new TimeSpan(13, 45, 30), new TimeSpan(0, 0, 0, 0, 0).Add(TimeSpan.FromTicks(1234567)), null,
         guidA, null, new TimeSpan(23, 59, 59) + TimeSpan.FromTicks(9999999), Guid.Empty]);
    var t0 = ResultMaterializer.FormatSqlTypedCell(typedT, 0);
    Check(t0.Display == "13:45:30" && t0.Kind == "time" && !t0.IsNull && !t0.Truncated, "ws4-time-whole-seconds");
    var t1 = ResultMaterializer.FormatSqlTypedCell(typedT, 1);
    Check(t1.Display == "00:00:00.1234567" && t1.Kind == "time", "ws4-time-100ns-precision");
    Check(ResultMaterializer.FormatSqlTypedCell(typedT, 2).IsNull, "ws4-time-null");
    var g0 = ResultMaterializer.FormatSqlTypedCell(typedT, 3);
    Check(g0.Display == "6f9619ff-8b86-d011-b42d-00c04fc964ff" && g0.Kind == "uniqueidentifier", "ws4-guid-D-format");
    Check(ResultMaterializer.FormatSqlTypedCell(typedT, 4).IsNull, "ws4-guid-null");
    Check(ResultMaterializer.FormatSqlTypedCell(typedT, 5).Display == "23:59:59.9999999", "ws4-time-max");
    Check(ResultMaterializer.FormatSqlTypedCell(typedT, 6).Display == "00000000-0000-0000-0000-000000000000", "ws4-guid-empty");

    var prevCultureT = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
        Check(ResultMaterializer.FormatSqlTypedCell(typedT, 0).Display == "13:45:30", "ws4-time-culture-invariant");
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = prevCultureT; }

    // Fixture (non-SqlDataReader) fallback path through ReadResultSetAsync.
    using var tgReader = new TypedValueReader(
        ["t", "g"],
        ["time", "uniqueidentifier"],
        [typeof(TimeSpan), typeof(Guid)],
        [
            [new TimeSpan(8, 5, 0), guidA],
            [null, null]
        ]);
    var tgOutcome = await ResultMaterializer.ReadResultSetAsync(tgReader, new ResultBudget(), CancellationToken.None);
    Check(tgOutcome.RowFailure is null && tgOutcome.Set.Rows.Count == 2, "ws4-read-resultset-no-failure");
    Check(tgOutcome.Set.Rows[0][0].Display == "08:05:00" && tgOutcome.Set.Rows[0][0].Kind == "time", "ws4-read-time");
    Check(tgOutcome.Set.Rows[0][1].Display == "6f9619ff-8b86-d011-b42d-00c04fc964ff" && tgOutcome.Set.Rows[0][1].Kind == "uniqueidentifier", "ws4-read-guid");
    Check(tgOutcome.Set.Rows[1].All(c => c.IsNull), "ws4-read-nulls");

    // Export: kind-only inference, CSV (spreadsheet-safe) and JSON strings.
    var tgSet = new ResultSet
    {
        Columns = ["t", "g", "tn", "gn"],
        Rows =
        [[
            ResultMaterializer.FormatSqlTypedCell(typedT, 1),
            ResultMaterializer.FormatSqlTypedCell(typedT, 3),
            ResultMaterializer.FormatSqlTypedCell(typedT, 2),
            ResultMaterializer.FormatSqlTypedCell(typedT, 4)
        ]]
    };
    var tgView = ResultSetView.FromResultSet(tgSet);
    Check(tgView.Columns[0].SqlType == "time" && tgView.Columns[1].SqlType == "uniqueidentifier", "ws4-infer-time-guid");
    var plainTg = CsvExporter.ToCsv(tgView).Split("\r\n")[1];
    Check(plainTg == "00:00:00.1234567,6f9619ff-8b86-d011-b42d-00c04fc964ff,,", "ws4-csv-plain");
    var safeTg = CsvExporter.ToCsv(tgView, new CsvOptions { SpreadsheetSafe = true }).Split("\r\n")[1];
    Check(safeTg == plainTg, "ws4-csv-spreadsheet-safe-unprefixed");
    var tgJson = System.Text.Json.JsonDocument.Parse(JsonExporter.ToJson(tgView)).RootElement
        .GetProperty("resultSets")[0];
    var tgRow = tgJson.GetProperty("rows")[0];
    Check(tgRow[0].ValueKind == System.Text.Json.JsonValueKind.String && tgRow[0].GetString() == "00:00:00.1234567", "ws4-json-time-string");
    Check(tgRow[1].ValueKind == System.Text.Json.JsonValueKind.String && tgRow[1].GetString() == "6f9619ff-8b86-d011-b42d-00c04fc964ff", "ws4-json-guid-string");
    Check(tgRow[2].ValueKind == System.Text.Json.JsonValueKind.Null && tgRow[3].ValueKind == System.Text.Json.JsonValueKind.Null, "ws4-json-null");
    Check(tgJson.GetProperty("columns")[0].GetProperty("sqlType").GetString() == "time"
          && tgJson.GetProperty("columns")[1].GetProperty("sqlType").GetString() == "uniqueidentifier", "ws4-json-sqltype");
    Pass("ws4-time-guid-types");
}

// --- WS4-T2: retained-row sort / filter helpers (never re-query; operate on retained rows only) ---
{
    ResultSetView MakeView(bool truncated, params (string id, string name, string? n)[] rows) => new()
    {
        Columns = [new ResultColumn("id", "int"), new ResultColumn("name", "nvarchar"), new ResultColumn("amt", "decimal")],
        Truncated = truncated,
        Rows = rows.Select(r => (IReadOnlyList<ResultCell>)
        [
            ResultCell.Of(r.id),
            r.name is null ? ResultCell.Null : ResultCell.Of(r.name),
            r.n is null ? ResultCell.Null : ResultCell.Of(r.n)
        ]).ToList()
    };

    var sv = MakeView(false,
        ("10", "banana", "2.50"),
        ("2", null!, "-1"),
        ("33", "Apple", null),
        ("4", "cherry", "100"));

    // numeric column sorts numerically (not lexically), NULLs last in both directions
    var byIdAsc = sv.SortByColumn(0, descending: false);
    Check(string.Join(",", byIdAsc.Rows.Select(r => r[0].Display)) == "2,4,10,33", "ws4-sort-numeric-asc");
    var byIdDesc = sv.SortByColumn(0, descending: true);
    Check(string.Join(",", byIdDesc.Rows.Select(r => r[0].Display)) == "33,10,4,2", "ws4-sort-numeric-desc");

    var byAmtAsc = sv.SortByColumn(2, descending: false);
    Check(string.Join(",", byAmtAsc.Rows.Select(r => r[2].IsNull ? "NULL" : r[2].Display)) == "-1,2.50,100,NULL", "ws4-sort-nulls-last-asc");
    var byAmtDesc = sv.SortByColumn(2, descending: true);
    Check(string.Join(",", byAmtDesc.Rows.Select(r => r[2].IsNull ? "NULL" : r[2].Display)) == "100,2.50,-1,NULL", "ws4-sort-nulls-last-desc");

    // text sort: case-insensitive; NULL (not empty string) last
    var byName = sv.SortByColumn(1, descending: false);
    Check(string.Join(",", byName.Rows.Select(r => r[1].IsNull ? "NULL" : r[1].Display)) == "Apple,banana,cherry,NULL", "ws4-sort-text-ci-nulls-last");

    // 38-digit decimals compare exactly (no System.Decimal / double narrowing)
    var big = MakeView(false,
        ("1", "a", "12345678901234567890123456789012345679"),
        ("2", "b", "12345678901234567890123456789012345678"));
    Check(big.SortByColumn(2, false).Rows[0][0].Display == "2", "ws4-sort-decimal38-exact");

    // stable: equal keys keep original order; source view untouched
    var ties = MakeView(false, ("1", "x", "5"), ("2", "x", "5"), ("3", "x", "5"));
    Check(string.Join(",", ties.SortByColumn(1, true).Rows.Select(r => r[0].Display)) == "1,2,3", "ws4-sort-stable");
    Check(sv.Rows[0][0].Display == "10", "ws4-sort-does-not-mutate-source");

    // Truncated label propagates; complete sets are not labelled truncated
    var trunc = MakeView(true, ("2", "b", "1"), ("1", "a", "2"));
    var truncSorted = trunc.SortByColumn(0, false);
    Check(truncSorted.Truncated && truncSorted.ScopeLabel is not null
          && truncSorted.ScopeLabel.Contains("retained", StringComparison.OrdinalIgnoreCase)
          && truncSorted.ScopeLabel.Contains("more rows", StringComparison.OrdinalIgnoreCase), "ws4-sort-truncated-label");
    var completeSorted = sv.SortByColumn(0, false);
    Check(!completeSorted.Truncated && completeSorted.ScopeLabel is not null
          && !completeSorted.ScopeLabel.Contains("more rows", StringComparison.OrdinalIgnoreCase), "ws4-sort-complete-label");
    Check(trunc.FilterContains("a").Truncated && trunc.FilterContains("a").ScopeLabel!.Contains("more rows", StringComparison.OrdinalIgnoreCase), "ws4-filter-truncated-label");

    // Filter: case-insensitive substring, all columns, or one column; NULL never matches
    Check(sv.FilterContains("APPLE").Rows.Count == 1, "ws4-filter-ci");
    Check(sv.FilterContains("an").Rows.Count == 1 && sv.FilterContains("an").Rows[0][0].Display == "10", "ws4-filter-substring");
    Check(sv.FilterContains("NULL").Rows.Count == 0, "ws4-filter-null-not-text-NULL");
    Check(sv.FilterContains("e", columnOrdinal: 1).Rows.Count == 2, "ws4-filter-column-scope");
    Check(sv.FilterContains("33", columnOrdinal: 1).Rows.Count == 0, "ws4-filter-column-scope-excludes-others");
    Check(sv.FilterContains("").Rows.Count == 4 && sv.FilterContains(null).Rows.Count == 4, "ws4-filter-empty-returns-all");
    Check(sv.FilterContains("zzz").Rows.Count == 0, "ws4-filter-no-match");
    Check(sv.FilterContains("1").SortByColumn(0, true).Rows.Count == 3, "ws4-filter-then-sort-compose");

    // Index helpers used by the grid (keeps CellValue Truncated flags / row numbers)
    Check(string.Join(",", sv.SortedRowIndexes(0, false)) == "1,3,0,2", "ws4-sorted-indexes");
    Check(string.Join(",", sv.FilterRowIndexes("an", null)) == "0", "ws4-filter-indexes");

    try { sv.SortByColumn(9, false); throw new Exception("FAIL ws4-sort-bad-ordinal"); }
    catch (ArgumentOutOfRangeException) { }

    // Exports of a sorted/filtered truncated view stay marked incomplete
    Check(JsonExporter.ToJson(truncSorted).Contains("\"truncated\": true", StringComparison.Ordinal), "ws4-sort-export-still-truncated");
    Pass("ws4-retained-sort-filter");
}

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

    // F7: structured batches with source spans (LF and CRLF).
    static void CheckSpans(string script, string label)
    {
        var parsed = GoBatchSplitter.Parse(script);
        foreach (var batch in parsed.Batches)
        {
            Check(batch.Length == batch.Text.Length, label + "-length");
            Check(string.CompareOrdinal(script, batch.Start, batch.Text, 0, batch.Length) == 0, label + "-span-text");
        }
    }

    {
        var lf = "SELECT 1;\nGO\n  SELECT 2;  \nGO 2\nSELECT 3";
        var p = GoBatchSplitter.Parse(lf);
        Check(p.HasSeparator && p.Batches.Count == 3 && p.ExpandedBatchCount == 4, "go-parse-shape");
        Check(p.Batches[0].Start == 0 && p.Batches[0].Length == 9 && p.Batches[0].StartLine == 1, "go-parse-span-first");
        Check(p.Batches[1].Text == "SELECT 2;" && p.Batches[1].Start == lf.IndexOf("SELECT 2;", StringComparison.Ordinal)
              && p.Batches[1].StartLine == 3 && p.Batches[1].RepeatCount == 2, "go-parse-span-trimmed-lf");
        Check(p.Batches[2].Text == "SELECT 3" && p.Batches[2].StartLine == 5 && p.Batches[0].RepeatCount == 1, "go-parse-lines");
        CheckSpans(lf, "go-span-lf");

        var crlf = "SELECT 1;\r\nGO\r\n\r\n  SELECT 2;\r\nGO 3\r\nSELECT 3\r\n";
        var c = GoBatchSplitter.Parse(crlf);
        Check(c.Batches.Count == 3 && c.ExpandedBatchCount == 5, "go-parse-crlf-shape");
        Check(c.Batches[1].Text == "SELECT 2;" && c.Batches[1].Start == crlf.IndexOf("SELECT 2;", StringComparison.Ordinal)
              && c.Batches[1].StartLine == 4 && c.Batches[1].RepeatCount == 3, "go-parse-crlf-span");
        Check(c.Batches[2].Text == "SELECT 3" && c.Batches[2].StartLine == 6, "go-parse-crlf-line");
        CheckSpans(crlf, "go-span-crlf");
        CheckSpans("a\rGO\rb", "go-span-cr");

        // Compatibility path still expands repeats.
        var compat = GoBatchSplitter.Split(crlf);
        Check(compat.Count == 5 && compat[1] == "SELECT 2;" && compat[3] == "SELECT 2;" && compat[4] == "SELECT 3", "go-split-compat");
        Pass("go-parse-source-spans");
    }

    // F7: unsupported SQLCMD / malformed GO rejected with location.
    {
        SqlScriptRejectedException? Reject(string sql)
        {
            try { GoBatchSplitter.Parse(sql); return null; }
            catch (SqlScriptRejectedException ex) { return ex; }
        }

        var setvar = "INSERT t VALUES (1)\nGO\n:setvar Name Value\nSELECT $(Name)";
        var r = Reject(setvar);
        Check(r is not null && r.Kind == SqlScriptRejectionKind.SqlcmdDirective, "sqlcmd-setvar-rejected");
        Check(r!.Line == 3 && r.Column == 1 && r.Offset == setvar.IndexOf(":setvar", StringComparison.Ordinal), "sqlcmd-setvar-location");
        Check(r.Message.Contains("line 3", StringComparison.OrdinalIgnoreCase) && r.Message.Contains(":setvar", StringComparison.Ordinal), "sqlcmd-setvar-message");

        var inc = "SELECT 1\r\nGO\r\n   :r C:\\scripts\\evil.sql\r\n";
        var ri = Reject(inc);
        Check(ri is not null && ri.Line == 3 && ri.Column == 4 && ri.Offset == inc.IndexOf(":r", StringComparison.Ordinal), "sqlcmd-r-location-crlf");

        foreach (var directive in new[] { ":connect srv", ":on error exit", ":out file.txt", ":ed", ":!! ls", ":quit", ":exit(SELECT 1)", ":ServerList", ":XML ON", ":Reset", ":list" })
            Check(Reject("SELECT 1\n" + directive) is { Kind: SqlScriptRejectionKind.SqlcmdDirective, Line: 2 }, "sqlcmd-rejected " + directive);

        // Directive-looking text inside strings/comments/brackets is not a directive.
        Check(Reject("SELECT 'a\n:setvar x y\n'") is null, "sqlcmd-in-string-ignored");
        Check(Reject("/* a\n:r file\n*/ SELECT 1") is null, "sqlcmd-in-block-comment-ignored");
        Check(Reject("/* a /* n\n:r file\n*/\n:r x\n*/ SELECT 1") is null, "sqlcmd-in-nested-comment-ignored");
        Check(Reject("SELECT [a\n:r b]") is null, "sqlcmd-in-bracket-ignored");
        Check(Reject("-- :setvar x\nSELECT 1") is null, "sqlcmd-in-line-comment-ignored");
        Check(Reject("SELECT 1 AS x -- :r\nSELECT 2") is null, "sqlcmd-trailing-comment-ignored");

        // Malformed GO lines are rejected, not forwarded to the provider.
        foreach (var bad in new[] { "GO abc", "GO 5 SELECT 1", "GO;", "GO -1", "GO 2 3" })
        {
            var rb = Reject("SELECT 1\nGO\nSELECT 2\n" + bad);
            Check(rb is { Kind: SqlScriptRejectionKind.MalformedGo, Line: 4 }, "go-malformed-rejected " + bad);
        }
        Check(Reject("SELECT 1\nGO 0") is { Kind: SqlScriptRejectionKind.RepeatCountOutOfRange, Line: 2 }, "go-zero-located");
        Check(Reject("SELECT 1\nGO 1001") is { Kind: SqlScriptRejectionKind.RepeatCountOutOfRange }, "go-over-max-located");
        Check(Reject("SELECT 1\nGO 1000") is null, "go-max-repeat-ok");
        Check(Reject("SELECT 1\nGO 1000\nSELECT 2\nGO 2") is { Kind: SqlScriptRejectionKind.TooManyBatches, Line: 4 }, "go-total-expansion-bounded");
        Check(Reject("SELECT 1\nGO 600\nSELECT 2\nGO 400\nSELECT 3") is { Kind: SqlScriptRejectionKind.TooManyBatches }, "go-total-expansion-bounded-2");
        Check(Reject("SELECT 1\nGO 600\nSELECT 2\nGO 400") is null, "go-total-expansion-exact-ok");

        // Still-valid forms.
        Check(Reject("SELECT 1\nGO -- trailing\nSELECT 2\ngo 3 -- c\nSELECT 3") is null, "go-comment-forms-ok");
        Check(Reject("GOTO label") is null && Reject("SELECT 'GO abc'") is null && Reject("SELECT 1 GO abc") is null, "go-words-ok");
        Check(Reject("SELECT a\nFROM t WHERE a = 1 -- ok\n/* GO abc */") is null, "go-in-comment-ok");
        Check(Reject("/* x /* y\nGO abc\n*/ z\n*/ SELECT 1") is null, "go-malformed-in-nested-comment-ignored");
        Check(Reject("SELECT 'x\nGO abc\n'") is null, "go-malformed-in-string-ignored");

        // Rejection is ArgumentException for existing callers.
        try { GoBatchSplitter.Split("SELECT 1\n:r x"); throw new Exception("FAIL split-rejects-directive"); }
        catch (ArgumentException) { }
        try { GoBatchSplitter.ContainsSeparator("SELECT 1\n:setvar a b"); throw new Exception("FAIL contains-rejects-directive"); }
        catch (SqlScriptRejectedException) { }
        // T-SQL scope resolution must not be treated as SQLCMD.
        Check(Reject("SELECT *\nFROM\n::fn_helpcollations()") is null, "sqlcmd-scope-resolution-allowed");
        Pass("sqlcmd-preflight-rejection");
    }

    // F7: SqlSession.ExecuteAsync preflights the whole selection before executing batch 1.
    {
        var executed = new List<string>();
        await using var session = new SqlSession
        {
            BatchExecutorForTests = (text, _, _) =>
            {
                executed.Add(text);
                return Task.FromResult(new QueryResult { ExecutedSql = text, Server = "s", Database = "d", Completion = QueryCompletionKind.Succeeded });
            }
        };

        foreach (var script in new[]
        {
            "INSERT dbo.t VALUES (1)\nGO\n:setvar X 1\nSELECT 1",
            "INSERT dbo.t VALUES (1)\nGO\nINSERT dbo.t VALUES (2)\nGO\n:r evil.sql",
            "DELETE dbo.t\nGO\nGO nonsense\nSELECT 1",
            "DELETE dbo.t\nGO\nSELECT 1\nGO 0",
            "DROP TABLE dbo.t\n:connect other",
        })
        {
            try
            {
                await session.ExecuteAsync(script);
                throw new Exception("FAIL preflight-did-not-reject");
            }
            catch (SqlScriptRejectedException ex)
            {
                Check(ex.Line >= 2 && ex.Offset > 0 && ex.Message.Contains("line", StringComparison.OrdinalIgnoreCase), "preflight-error-location");
                Check(ex.Message.Contains("Nothing was executed", StringComparison.Ordinal), "preflight-error-nothing-executed");
            }
            Check(executed.Count == 0, "preflight-nothing-executed");
            Check(!session.HasActiveCommand, "preflight-no-inflight");
        }

        // Control: a valid script does reach the executor (batch 1 first).
        try { await session.ExecuteAsync("SELECT 1\nGO\nSELECT 2"); } catch (InvalidOperationException) { }
        Check(executed.Count >= 1 && executed[0] == "SELECT 1", "preflight-valid-script-executes");

        // Single-batch script (no GO) is preflighted too.
        executed.Clear();
        try { await session.ExecuteAsync("DELETE dbo.t\n:setvar x y"); throw new Exception("FAIL preflight-single-batch"); }
        catch (SqlScriptRejectedException) { }
        Check(executed.Count == 0, "preflight-single-batch-nothing-executed");

        // Without a connection, rejection must still win over "Not connected".
        await using var offline = new SqlSession();
        try { await offline.ExecuteAsync("SELECT 1\nGO\n:r x"); throw new Exception("FAIL preflight-offline"); }
        catch (SqlScriptRejectedException) { }
        Pass("sqlsession-preflight-before-execute");
    }
}

// D5: rejection locations are reported against the whole document, not the run selection.
{
    var executed = new List<string>();
    await using var session = new SqlSession
    {
        BatchExecutorForTests = (text, _, _) =>
        {
            executed.Add(text);
            return Task.FromResult(new QueryResult { ExecutedSql = text, Server = "s", Database = "d", Completion = QueryCompletionKind.Succeeded });
        }
    };

    var prefix = string.Concat(Enumerable.Repeat("\n", 50)); // document lines 1..50 are blank
    var selection = ":setvar Name Value\nSELECT 1";
    var document = prefix + selection + "\nSELECT 2";
    var selectionStart = prefix.Length;
    var origin = ScriptExecutionOrigin.FromDocument(document, selectionStart);
    Check(origin.DocumentLineOffset == 50 && origin.DocumentOffset == 50, "d5-origin-from-document");

    // Selection on document line 51: :setvar is the selection's first line.
    try
    {
        await session.ExecuteAsync(selection, origin);
        throw new Exception("FAIL d5-setvar-not-rejected");
    }
    catch (SqlScriptRejectedException ex)
    {
        Check(ex.Line == 51, "d5-setvar-document-line");
        Check(ex.Offset == selectionStart, "d5-setvar-document-offset");
        Check(ex.SelectionLine == 1 && ex.SelectionOffset == 0, "d5-setvar-selection-relative-kept");
        Check(ex.Message.Contains("line 51,", StringComparison.Ordinal), "d5-setvar-message-document-line");
        Check(document.AsSpan(ex.Offset).StartsWith(":setvar", StringComparison.Ordinal), "d5-setvar-offset-points-at-directive");
    }

    // GO rejected mid-selection: selection starts on document line 11 (10 lines before).
    var doc2 = string.Concat(Enumerable.Repeat("-- pad\n", 10)) + "SELECT 1\nGO\nSELECT 2\nGO nonsense\nSELECT 3";
    var start2 = doc2.IndexOf("SELECT 1", StringComparison.Ordinal);
    var sel2 = doc2[start2..];
    var origin2 = ScriptExecutionOrigin.FromDocument(doc2, start2);
    Check(origin2.DocumentLineOffset == 10, "d5-origin-line-offset");
    try
    {
        await session.ExecuteAsync(sel2, origin2);
        throw new Exception("FAIL d5-go-not-rejected");
    }
    catch (SqlScriptRejectedException ex)
    {
        Check(ex.Kind == SqlScriptRejectionKind.MalformedGo, "d5-go-kind");
        Check(ex.SelectionLine == 4, "d5-go-selection-line");
        Check(ex.Line == 14, "d5-go-document-line");
        Check(doc2.AsSpan(ex.Offset).StartsWith("GO nonsense", StringComparison.Ordinal), "d5-go-offset-points-at-go");
        Check(ex.Message.Contains("line 14,", StringComparison.Ordinal), "d5-go-message-document-line");
    }
    Check(executed.Count == 0, "d5-nothing-executed");

    // CRLF documents and column on a mid-line selection start.
    var doc3 = "a\r\nb\r\n  :r x";
    var origin3 = ScriptExecutionOrigin.FromDocument(doc3, doc3.IndexOf(":r", StringComparison.Ordinal));
    Check(origin3.DocumentLineOffset == 2, "d5-origin-crlf");
    var doc4 = "SELECT 1; :r x";
    var start4 = doc4.IndexOf(":r", StringComparison.Ordinal);
    var origin4 = ScriptExecutionOrigin.FromDocument(doc4, start4);
    Check(origin4.DocumentLineOffset == 0 && origin4.DocumentColumnOffset == start4, "d5-origin-column");
    try { await session.ExecuteAsync(doc4[start4..], origin4); throw new Exception("FAIL d5-column"); }
    catch (SqlScriptRejectedException ex) { Check(ex.Line == 1 && ex.Column == start4 + 1 && ex.Offset == start4, "d5-first-line-column-shifted"); }

    // No origin: behaviour unchanged (selection-relative == document-relative).
    try { await session.ExecuteAsync("SELECT 1\nGO\n:r x", (ScriptExecutionOrigin?)null); throw new Exception("FAIL d5-null-origin"); }
    catch (SqlScriptRejectedException ex) { Check(ex.Line == 3 && ex.SelectionLine == 3, "d5-null-origin-unchanged"); }

    // Origin must not be able to produce negative locations.
    try { _ = new ScriptExecutionOrigin(-1, 0); throw new Exception("FAIL d5-negative-origin"); }
    catch (ArgumentOutOfRangeException) { }
    Pass("d5-document-relative-rejection");
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
        Check(defaults.ResultsMaxRows == QueryLimits.MaxRowsPerSet && defaults.ConfirmDestructive && defaults.Theme == "System", "settings-defaults");
        Check(AppSettings.MaxResultsMaxRows == QueryLimits.MaxRowsPerSet, "settings-cap-matches-engine");
        settingsStore.Save(defaults with { EditorFontSize = 16, ResultsMaxRows = 500, Theme = "Dark", ConfirmDestructive = false });
        var savedSettings = new AppSettingsStore(root).Load();
        Check(savedSettings.EditorFontSize == 16 && savedSettings.ResultsMaxRows == 500 && savedSettings.Theme == "Dark" && !savedSettings.ConfirmDestructive, "settings-roundtrip");
        File.WriteAllText(settingsStore.FilePath, "garbage");
        Check(settingsStore.Load().ResultsMaxRows == QueryLimits.MaxRowsPerSet, "settings-corrupt-defaults");
        settingsStore.Save(new AppSettings { Theme = "Neon", ResultsMaxRows = -5, EditorFontSize = 1000 });
        var clamped = settingsStore.Load();
        Check(clamped.Theme == "System" && clamped.ResultsMaxRows == 1 && clamped.EditorFontSize == AppSettings.MaxFontSize, "settings-normalized");
        // A legacy file with the removed line-number preference and an over-cap row limit still loads and is clamped.
        File.WriteAllText(settingsStore.FilePath, "{\"showLineNumbers\":true,\"resultsMaxRows\":1000000,\"theme\":\"Dark\"}");
        var legacy = settingsStore.Load();
        Check(legacy.ResultsMaxRows == QueryLimits.MaxRowsPerSet && legacy.Theme == "Dark", "settings-legacy-clamped");
        Check(new AppSettings { ResultsMaxRows = 9_999 }.Normalize().ResultsMaxRows == QueryLimits.MaxRowsPerSet, "settings-over-cap-clamped");
        Check(new AppSettings { ResultsMaxRows = QueryLimits.MaxRowsPerSet }.Normalize().ResultsMaxRows == QueryLimits.MaxRowsPerSet, "settings-at-cap-kept");
        Check(new AppSettings { ResultsMaxRows = 0 }.Normalize().ResultsMaxRows == 1, "settings-zero-clamped");
        var layoutDefaults = new AppSettings();
        Check(layoutDefaults.SidebarWidth == 240 && layoutDefaults.SidebarVisible && layoutDefaults.ResultsHeight == 0, "settings-layout-defaults");
        var layoutClamped = new AppSettings { SidebarWidth = 9999, ResultsHeight = -3 }.Normalize();
        Check(layoutClamped.SidebarWidth == AppSettings.MaxSidebarWidth && layoutClamped.ResultsHeight == 0, "settings-layout-clamped");
        Check(new AppSettings { SidebarWidth = double.NaN }.Normalize().SidebarWidth == AppSettings.DefaultSidebarWidth, "settings-layout-nan");
        Check(new AppSettings { SidebarWidth = 10 }.Normalize().SidebarWidth == AppSettings.MinSidebarWidth, "settings-layout-min");
        settingsStore.Save(new AppSettings { SidebarWidth = 300, SidebarVisible = false, ResultsHeight = 260 });
        var layoutBack = settingsStore.Load();
        Check(layoutBack.SidebarWidth == 300 && !layoutBack.SidebarVisible && layoutBack.ResultsHeight == 260, "settings-layout-roundtrip");
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
    // Verifier D1: FromResultSet without explicit sqlTypes must still preserve numeric spreadsheet-safe export.
    {
        var withSqlTypes = new ResultSet
        {
            Columns = ["id", "delta", "flag"],
            SqlTypes = ["int", "int", "bit"],
            Rows =
            [
                [
                    CellValue.Text("1", "number"),
                    CellValue.Text("-5", "number"),
                    CellValue.Text("true", "bool")
                ]
            ]
        };
        var fromTypes = ResultSetView.FromResultSet(withSqlTypes);
        Check(fromTypes.Columns[1].SqlType == "int" && fromTypes.Columns[2].SqlType == "bit", "from-resultset-preserves-sqltypes");
        var safeTyped = CsvExporter.ToCsv(fromTypes, new CsvOptions { SpreadsheetSafe = true });
        Check(safeTyped.Contains("-5", StringComparison.Ordinal) && !safeTyped.Contains("'-5", StringComparison.Ordinal), "safe-csv-typed-negative-intact");

        var kindOnly = new ResultSet
        {
            Columns = ["delta"],
            Rows = [[CellValue.Text("-5", "number")]]
        };
        var fromKind = ResultSetView.FromResultSet(kindOnly);
        Check(fromKind.Columns[0].SqlType == "int", "from-resultset-infers-int-from-kind");
        var safeKind = CsvExporter.ToCsv(fromKind, new CsvOptions { SpreadsheetSafe = true });
        Check(safeKind.Contains("-5", StringComparison.Ordinal) && !safeKind.Contains("'-5", StringComparison.Ordinal), "safe-csv-kind-inferred-negative-intact");
        Pass("export-from-resultset-types");
    }
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

    // D8: TryApplySecret only applies for the current generation while still Waiting.
    {
        var session = new ProfileAutofillSession();
        var gen = session.BeginLookup(Profile(pid));
        Check(session.IsWaiting, "tryapply-waiting-after-begin");
        Check(!session.TryApplySecret(gen - 1, "secretA"), "tryapply-wrong-generation");
        Check(session.TryApplySecret(gen, "secretA"), "tryapply-ok");
        Check(!session.TryApplySecret(gen, "other"), "tryapply-second-apply-ignored");
        Check(session.ResolvePassword(Id(), "secretA").UsesRememberedSecret, "tryapply-first-secret-kept");

        var typedSession = new ProfileAutofillSession();
        var typedGen = typedSession.BeginLookup(Profile(pid));
        typedSession.NotifyPasswordEdited();
        Check(!typedSession.IsWaiting && !typedSession.TryApplySecret(typedGen, "secretA"), "tryapply-after-typed-ignored");
        Check(!typedSession.TryApplySecret(typedSession.Generation, "secretA"), "tryapply-after-typed-current-generation-ignored");
        Check(!typedSession.ResolvePassword(Id(), "secretA").UsesRememberedSecret, "tryapply-after-typed-not-remembered");

        var noRemember = new ProfileAutofillSession();
        var g = noRemember.BeginLookup(Profile(pid, remember: false));
        Check(!noRemember.TryApplySecret(g, "secretA"), "tryapply-not-requested-ignored");
        var empty = new ProfileAutofillSession();
        Check(!empty.TryApplySecret(empty.BeginLookup(Profile(pid)), ""), "tryapply-empty-ignored");
        Pass("autofill-tryapply-secret");
    }

    // D8: UI event sequence through the controller.
    {
        var pidB = Guid.NewGuid();

        // typed password, then late Keychain
        {
            var c = new ConnectAutofillController();
            var gen = c.SelectProfile(Profile(pid));
            Check(c.LookupPending && c.PasswordText == "", "ctl-typed-select-pending");
            c.UserTypedPassword("typedPw");
            var outcome = c.LookupCompleted(gen, "secretA");
            Check(outcome.Status == AutofillStatus.Stale && outcome.Secret is null, "ctl-typed-late-stale");
            Check(c.PasswordText == "typedPw" && !c.PasswordIsAutofilled, "ctl-typed-box-untouched");
            var d = c.ResolveForConnect(Id());
            Check(d.Allowed && d.Password == "typedPw" && !d.UsesRememberedSecret, "ctl-typed-connect-explicit");
            Pass("ctl-typed-then-late-keychain");
        }

        // change host, then late Keychain
        {
            var c = new ConnectAutofillController();
            var gen = c.SelectProfile(Profile(pid));
            Check(!c.UserChangedEndpoint(Id("hostB")), "ctl-host-pending-nothing-to-clear");
            Check(!c.LookupPending, "ctl-host-invalidated-pending");
            var outcome = c.LookupCompleted(gen, "secretA");
            Check(outcome.Status == AutofillStatus.Stale && c.PasswordText == "" && !c.PasswordIsAutofilled, "ctl-host-late-stale");
            var d = c.ResolveForConnect(Id("hostB"));
            Check(d.Allowed && d.Password == "" && !d.UsesRememberedSecret, "ctl-host-connect-no-secret");
            Pass("ctl-change-host-then-late-keychain");
        }

        // late Keychain applies, then change host before Connect
        {
            var c = new ConnectAutofillController();
            var gen = c.SelectProfile(Profile(pid));
            var outcome = c.LookupCompleted(gen, "secretA");
            Check(outcome.Status == AutofillStatus.Applied && c.PasswordText == "secretA" && c.PasswordIsAutofilled, "ctl-apply-fills-box");
            Check(c.ResolveForConnect(Id()).UsesRememberedSecret, "ctl-apply-connect-ok-same-endpoint");
            Check(c.UserChangedEndpoint(Id("hostB")), "ctl-apply-host-change-clears");
            Check(c.PasswordText == "" && !c.PasswordIsAutofilled, "ctl-apply-host-box-empty");
            var d = c.ResolveForConnect(Id("hostB"));
            Check(d.Allowed && d.Password == "" && !d.UsesRememberedSecret, "ctl-apply-host-connect-empty");

            // defence in depth: box not cleared by the UI, Connect still refuses
            var c2 = new ConnectAutofillController();
            c2.LookupCompleted(c2.SelectProfile(Profile(pid)), "secretA");
            var refused = c2.ResolveForConnect(Id("hostB"));
            Check(!refused.Allowed && !refused.UsesRememberedSecret && refused.Password == "", "ctl-apply-mismatch-refused");
            Check(c2.PasswordText == "", "ctl-apply-mismatch-box-cleared");
            Pass("ctl-keychain-then-change-host");
        }

        // Connect never sees A's Keychain secret for B (profile switch and late result)
        {
            var c = new ConnectAutofillController();
            var genA = c.SelectProfile(Profile(pid, "hostA"));
            var genB = c.SelectProfile(Profile(pidB, "hostB", user: "bob"));
            Check(genB > genA, "ctl-switch-generation-advances");
            Check(c.LookupCompleted(genA, "secretA").Status == AutofillStatus.Stale, "ctl-switch-a-stale");
            Check(c.PasswordText == "", "ctl-switch-box-empty-after-a");
            var idB = Id("hostB", user: "bob");
            var beforeB = c.ResolveForConnect(idB);
            Check(beforeB.Allowed && beforeB.Password == "" && !beforeB.UsesRememberedSecret, "ctl-switch-connect-before-b");
            Check(c.LookupCompleted(genB, "secretB").Status == AutofillStatus.Applied, "ctl-switch-b-applied");
            var forB = c.ResolveForConnect(idB);
            Check(forB.Allowed && forB.Password == "secretB" && forB.UsesRememberedSecret, "ctl-switch-connect-b-own-secret");
            Check(!c.ResolveForConnect(Id("hostA")).Allowed, "ctl-switch-b-secret-refused-at-a");
            Pass("ctl-connect-never-sees-other-secret");
        }

        // Keychain denial for the current generation, typing after apply, and Forget (dialog close / profile delete)
        {
            var c = new ConnectAutofillController();
            var gen = c.SelectProfile(Profile(pid));
            var failed = c.LookupFailed(gen, new InvalidOperationException("denied"));
            Check(failed.Status == AutofillStatus.Failed && !c.LookupPending && c.PasswordText == "" && !c.PasswordIsAutofilled, "ctl-denied-current-surfaces-failure");
            var afterDenied = c.ResolveForConnect(Id());
            Check(afterDenied.Allowed && afterDenied.Password == "" && !afterDenied.UsesRememberedSecret, "ctl-denied-connect-needs-explicit-password");

            var c2 = new ConnectAutofillController();
            c2.LookupCompleted(c2.SelectProfile(Profile(pid)), "secretA");
            c2.UserTypedPassword("typedOverAutofill");
            var typedDecision = c2.ResolveForConnect(Id());
            Check(c2.PasswordText == "typedOverAutofill" && !c2.PasswordIsAutofilled && typedDecision.Allowed && !typedDecision.UsesRememberedSecret, "ctl-typed-over-autofill-is-explicit");

            var c3 = new ConnectAutofillController();
            var gen3 = c3.SelectProfile(Profile(pid));
            c3.LookupCompleted(gen3, "secretA");
            c3.Forget();
            Check(c3.PasswordText == "" && !c3.PasswordIsAutofilled, "ctl-forget-clears-box");
            Check(c3.LookupCompleted(gen3, "secretA").Status == AutofillStatus.Stale && c3.PasswordText == "", "ctl-forget-then-late-result-stale");
            Check(!c3.ResolveForConnect(Id()).UsesRememberedSecret, "ctl-forget-connect-no-secret");
            Pass("ctl-denied-typed-forget-ordering");
        }

        // end-to-end with a delayed store: Window-style sequence
        {
            var store = new DelayedSecretStore();
            var c = new ConnectAutofillController();
            var gen = c.SelectProfile(Profile(pid));
            var read = store.GetAsync(pidKey);
            c.UserChangedEndpoint(Id("hostB"));
            store.Release(pidKey, "secretA");
            var outcome = c.LookupCompleted(gen, await read);
            Check(outcome.Status == AutofillStatus.Stale && c.PasswordText == "", "ctl-delayed-store-discarded");
            var late = c.ResolveForConnect(Id("hostB"));
            Check(late.Password == "" && !late.UsesRememberedSecret, "ctl-delayed-store-connect-empty");

            // failure for a superseded generation is silent
            var g2 = c.SelectProfile(Profile(pid));
            c.UserTypedPassword("x");
            Check(c.LookupFailed(g2, new InvalidOperationException("denied")).Status == AutofillStatus.Stale, "ctl-failed-stale-silent");
            Pass("ctl-delayed-store-sequence");
        }
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

    // Verifier D4: a throwing DisposeAsync must never replace or lose the computed result.
    var disposeBoom = () => new InvalidOperationException("dispose exploded");
    var rbFailDisposeFail = new FakeEditTransactionFactory(1, 0)
    {
        RollbackFails = new InvalidOperationException("connection broken"),
        DisposeFails = disposeBoom()
    };
    var rbFailDisposeFailResult = await EditRowsBatch.ApplyAsync(rbFailDisposeFail, cmds);
    Check(rbFailDisposeFailResult.Outcome == EditBatchOutcome.OutcomeUnknown && rbFailDisposeFailResult.RequiresReload
        && rbFailDisposeFailResult.FailedIndex == 1 && rbFailDisposeFailResult.WritesValidated == 1
        && rbFailDisposeFailResult.Message.Contains("Rollback could not be confirmed")
        && rbFailDisposeFailResult.Message.Contains("dispose exploded"), "batch-dispose-throw-keeps-outcome-unknown");

    var commitFailDisposeFail = new FakeEditTransactionFactory(1, 1)
    {
        CommitFails = new InvalidOperationException("connection lost"),
        DisposeFails = disposeBoom()
    };
    var commitFailDisposeFailResult = await EditRowsBatch.ApplyAsync(commitFailDisposeFail, cmds);
    Check(commitFailDisposeFailResult.Outcome == EditBatchOutcome.OutcomeUnknown && commitFailDisposeFailResult.RequiresReload
        && commitFailDisposeFailResult.Message.Contains("Commit could not be confirmed")
        && commitFailDisposeFailResult.Message.Contains("dispose exploded"), "batch-dispose-throw-after-commit-failure");

    var conflictDisposeFail = new FakeEditTransactionFactory(1, 0) { DisposeFails = disposeBoom() };
    var conflictDisposeFailResult = await EditRowsBatch.ApplyAsync(conflictDisposeFail, cmds);
    Check(conflictDisposeFailResult.Outcome == EditBatchOutcome.RolledBackConflict && conflictDisposeFailResult.FailedIndex == 1
        && conflictDisposeFailResult.WritesValidated == 1 && !conflictDisposeFailResult.Committed
        && conflictDisposeFailResult.Message.Contains("conflicted") && conflictDisposeFailResult.Message.Contains("dispose exploded"),
        "batch-dispose-throw-preserves-conflict");

    var errorDisposeFail = new FakeEditTransactionFactory(1, new InvalidOperationException("synthetic provider failure")) { DisposeFails = disposeBoom() };
    var errorDisposeFailResult = await EditRowsBatch.ApplyAsync(errorDisposeFail, cmds);
    Check(errorDisposeFailResult.Outcome == EditBatchOutcome.RolledBackError && errorDisposeFailResult.Message.Contains("dispose exploded"),
        "batch-dispose-throw-preserves-rolled-back-error");

    // Committed + dispose throws: the connection state is no longer trustworthy, so escalate.
    var commitDisposeFail = new FakeEditTransactionFactory(1, 1) { DisposeFails = disposeBoom() };
    var commitDisposeFailResult = await EditRowsBatch.ApplyAsync(commitDisposeFail, cmds);
    Check(commitDisposeFailResult.Outcome == EditBatchOutcome.OutcomeUnknown && commitDisposeFailResult.RequiresReload
        && !commitDisposeFailResult.Committed && commitDisposeFailResult.WritesValidated == 2
        && commitDisposeFailResult.Message.Contains("dispose exploded"), "batch-dispose-throw-after-commit-escalates");

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

    // D3: character concurrency columns must be compared with a binary collation.
    var chars = EditRowsPolicy.Evaluate(["Id"],
        [C("Id", "int"), C("Qty", "int"), C("A", "char"), C("B", "varchar"), C("N", "nchar"), C("V", "nvarchar"), C("Ts", "datetime")], false);
    Check(chars.IsSupported && chars.CharacterConcurrencyColumns!.SequenceEqual(["A", "B", "N", "V"]), "policy-character-concurrency-columns");
    Check(rv.CharacterConcurrencyColumns is null or { Count: 0 }, "policy-rowversion-no-character-columns");
    Check(plain.CharacterConcurrencyColumns!.SequenceEqual(["Name"]), "policy-plain-character-columns");
    Check(EditRowsPolicy.IsCharacterType("NVarChar") && !EditRowsPolicy.IsCharacterType("int") && !EditRowsPolicy.IsCharacterType("text"), "policy-is-character-type");
    Check(EditRowsPolicy.CharacterConcurrencyCollation == "Latin1_General_BIN2", "policy-character-collation-name");
}

// D3: case-insensitive collation must not hide 'abc' -> 'ABC' / trailing-space changes in the optimistic predicate
{
    var t = new TableRef("App", "dbo", "T");
    var k = new RowKey(["Id"], [1]);
    var orig = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "abc", ["Qty"] = 4, ["Note"] = null, ["Code"] = "x" };
    var chars = new HashSet<string>(["Name", "Note", "Code"], StringComparer.OrdinalIgnoreCase);
    var u = EditRowsSql.BuildUpdate(t, k, new Dictionary<string, object?> { ["Qty"] = 5 }, orig, null, chars);
    Check(u.CommandText.Contains("(CAST([Name] AS nvarchar(max)) + N'|') COLLATE Latin1_General_BIN2 = (CAST(@o0 AS nvarchar(max)) + N'|')", StringComparison.Ordinal), "d3-update-nvarchar-bin2");
    Check(u.CommandText.Contains("AND [Qty] = @o1", StringComparison.Ordinal), "d3-update-nonchar-unchanged");
    Check(u.CommandText.Contains("AND [Note] IS NULL", StringComparison.Ordinal), "d3-update-null-char-is-null");
    Check(u.CommandText.Contains("CAST([Code] AS nvarchar(max)) + N'|') COLLATE Latin1_General_BIN2 = (CAST(@o2 AS", StringComparison.Ordinal), "d3-update-second-char-column");
    Check(u.CommandText.Contains("WHERE [Id] = @k0", StringComparison.Ordinal) && !u.CommandText.Contains("[Id]) COLLATE", StringComparison.Ordinal), "d3-key-unchanged");
    Check(u.Parameters.Single(p => p.Name == "@o0").Value is "abc", "d3-update-still-parameterised");

    var noChars = EditRowsSql.BuildUpdate(t, k, new Dictionary<string, object?> { ["Qty"] = 5 }, orig, new HashSet<string> { "Name", "Note", "Code" }, chars);
    Check(!noChars.CommandText.Contains("COLLATE", StringComparison.Ordinal), "d3-excluded-char-column-has-no-collate");

    var legacy = EditRowsSql.BuildUpdate(t, k, new Dictionary<string, object?> { ["Qty"] = 5 }, new Dictionary<string, object?> { ["Qty"] = 4 });
    Check(!legacy.CommandText.Contains("COLLATE", StringComparison.Ordinal), "d3-no-char-no-collate");

    var d = EditRowsSql.BuildDelete(t, k, orig, null, chars);
    Check(d.CommandText.Contains("COLLATE Latin1_General_BIN2", StringComparison.Ordinal), "d3-delete-bin2");
    Pass("edit-rows-binary-collation");
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

// F13: scripting an object opens a NEW tab; never appends to the active (possibly dirty) editor
{
    var plan = ScriptFromObjectPlan.Create("SalesDb", false, "SalesDb", "dbo", "Orders");
    Check(plan.NewTab, "script-plan-new-tab");
    Check(plan.Sql == SqlIdentity.ScriptSelectTop("SalesDb", "dbo", "Orders"), "script-plan-sql-select-only");
    var dirtyBuffer = "UPDATE x SET y = 1 -- unsaved\n";
    Check(!plan.Sql.Contains("UPDATE") && !plan.Sql.StartsWith(dirtyBuffer, StringComparison.Ordinal), "script-plan-not-concatenated-with-dirty-buffer");
    Check(plan.TargetDatabase == "SalesDb" && !plan.RequiresDatabaseChange, "script-plan-same-database");

    var other = ScriptFromObjectPlan.Create("master", false, "SalesDb", "dbo", "Orders");
    Check(other.NewTab && other.TargetDatabase == "SalesDb" && other.RequiresDatabaseChange, "script-plan-other-database-needs-change");
    Check(!ScriptFromObjectPlan.Create("SALESDB", false, "salesdb", "dbo", "o").RequiresDatabaseChange, "script-plan-database-case-insensitive");
    Check(ScriptFromObjectPlan.Create("SalesDb", true, "SalesDb", "dbo", "Orders").RequiresDatabaseChange, "script-plan-unknown-target-forces-change");
    Check(ScriptFromObjectPlan.Create(null, false, "SalesDb", "dbo", "Orders").RequiresDatabaseChange, "script-plan-no-session-db-forces-change");

    var hostile = ScriptFromObjectPlan.Create("master", false, "A]B", "s]x", "t]; DROP TABLE z;--");
    Check(hostile.Sql.Contains("[t]]; DROP TABLE z;--]", StringComparison.Ordinal) && hostile.TargetDatabase == "A]B", "script-plan-quotes-identifiers");
    foreach (var bad in new (string?, string?, string?)[] { (null, "s", "o"), ("d", null, "o"), ("d", "s", null), ("", "s", "o") })
    {
        try
        {
            ScriptFromObjectPlan.Create("d", false, bad.Item1, bad.Item2, bad.Item3);
            throw new Exception("FAIL script-plan-incomplete-identity");
        }
        catch (ArgumentException) { }
    }
    Pass("script-from-object-plan");

    // Object Explorer truncation honesty
    Check(CatalogTruncation.Describe(5, false, false) is null, "oe-truncation-none");
    var capped = CatalogTruncation.Describe(5000, true, false);
    Check(capped is not null && capped.Contains("5000") && capped.Contains("truncated", StringComparison.OrdinalIgnoreCase), "oe-truncation-row-cap");
    var budget = CatalogTruncation.Describe(12, true, true);
    Check(budget is not null && budget.Contains("12") && budget.Contains("budget", StringComparison.OrdinalIgnoreCase), "oe-truncation-byte-budget");
    Pass("oe-truncation-honesty");
}

// D2: external file edits must survive Save
{
    var dir = Path.Combine(Path.GetTempPath(), "sqloryn-d2-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        // 1. Load, external edit, Save -> refused; external bytes intact
        var fileA = Path.Combine(dir, "a.sql");
        File.WriteAllText(fileA, "SELECT 'original';");
        var docA = new ScriptDocument();
        await docA.LoadFileAsync(fileA);
        File.WriteAllText(fileA, "SELECT 'external edit, different length';");
        docA.SetText("SELECT 'mine';");
        var refused = false;
        try { await docA.SaveAsync(); }
        catch (ExternalDocumentChangedException) { refused = true; }
        Check(refused, "d2-external-edit-save-refused");
        Check(File.ReadAllText(fileA) == "SELECT 'external edit, different length';", "d2-external-edit-survives-save");
        // 3. refused save leaves the user's buffer dirty
        Check(docA.IsDirty && docA.Text == "SELECT 'mine';", "d2-refused-save-keeps-dirty-buffer");
        Check(Directory.GetFiles(dir, "*.tmp").Length == 0, "d2-refused-save-removes-temp");

        // same length, different bytes (mtime-granularity proof)
        var fileB = Path.Combine(dir, "b.sql");
        File.WriteAllText(fileB, "AAAA");
        var docB = new ScriptDocument();
        await docB.LoadFileAsync(fileB);
        var mtime = File.GetLastWriteTimeUtc(fileB);
        File.WriteAllText(fileB, "BBBB");
        File.SetLastWriteTimeUtc(fileB, mtime);
        docB.SetText("MINE");
        var refusedB = false;
        try { await docB.SaveAsync(); }
        catch (ExternalDocumentChangedException) { refusedB = true; }
        Check(refusedB && File.ReadAllText(fileB) == "BBBB", "d2-same-length-same-mtime-edit-detected");

        // external edit landing between capture and write is also caught
        var fileC = Path.Combine(dir, "c.sql");
        File.WriteAllText(fileC, "C0");
        var docC = new ScriptDocument();
        await docC.LoadFileAsync(fileC);
        docC.SetText("C-mine");
        var refusedC = false;
        try
        {
            await docC.SaveAsync(betweenCaptureAndWrite: () =>
            {
                File.WriteAllText(fileC, "C-external");
                return Task.CompletedTask;
            });
        }
        catch (ExternalDocumentChangedException) { refusedC = true; }
        Check(refusedC && File.ReadAllText(fileC) == "C-external" && docC.IsDirty, "d2-external-edit-between-capture-and-write");

        // 2. no external change: Save succeeds, dirty cleared, and repeated saves keep working
        var fileD = Path.Combine(dir, "d.sql");
        File.WriteAllText(fileD, "D0");
        var docD = new ScriptDocument();
        await docD.LoadFileAsync(fileD);
        docD.SetText("D1");
        await docD.SaveAsync();
        Check(File.ReadAllText(fileD) == "D1" && !docD.IsDirty, "d2-unchanged-save-succeeds-and-clears-dirty");
        docD.SetText("D2");
        await docD.SaveAsync();
        Check(File.ReadAllText(fileD) == "D2" && !docD.IsDirty, "d2-second-save-after-own-save-succeeds");

        // save-as new path, then save again without external change
        var fileE = Path.Combine(dir, "e.sql");
        var docE = new ScriptDocument();
        docE.SetText("E0");
        await docE.SaveAsync(fileE);
        docE.SetText("E1");
        await docE.SaveAsync();
        Check(File.ReadAllText(fileE) == "E1" && !docE.IsDirty, "d2-save-as-then-save");

        // explicit force overwrites after confirmation
        File.WriteAllText(fileE, "E-external");
        docE.SetText("E2");
        var refusedE = false;
        try { await docE.SaveAsync(); }
        catch (ExternalDocumentChangedException) { refusedE = true; }
        Check(refusedE && File.ReadAllText(fileE) == "E-external", "d2-refused-before-force");
        await docE.SaveAsync(forceSave: true);
        Check(File.ReadAllText(fileE) == "E2" && !docE.IsDirty, "d2-force-save-overwrites");
        docE.SetText("E3");
        await docE.SaveAsync();
        Check(File.ReadAllText(fileE) == "E3", "d2-save-after-force-resumes-tracking");

        // external delete is also a change
        File.Delete(fileE);
        docE.SetText("E4");
        var refusedDel = false;
        try { await docE.SaveAsync(); }
        catch (ExternalDocumentChangedException) { refusedDel = true; }
        Check(refusedDel && !File.Exists(fileE) && docE.IsDirty, "d2-external-delete-refused");
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
    Pass("d2-external-edit-survives-save");
}

// Object Explorer: keys, indexes, functions under a schema
{
    var keys = MetadataCatalog.ListKeys("My]Db", "dbo");
    Check(keys.Contains("[My]]Db].sys.key_constraints", StringComparison.Ordinal) && keys.Contains("sys.foreign_keys", StringComparison.Ordinal) &&
          keys.Contains("@schema", StringComparison.Ordinal) && !keys.Contains("'dbo'", StringComparison.Ordinal), "metadata-keys");
    var indexes = MetadataCatalog.ListIndexes("d", "s");
    Check(indexes.Contains("[d].sys.indexes", StringComparison.Ordinal) && indexes.Contains("i.index_id > 0", StringComparison.Ordinal) &&
          indexes.Contains("@schema", StringComparison.Ordinal), "metadata-indexes");
    var functions = MetadataCatalog.ListFunctions("d", "s");
    Check(functions.Contains("'FN'", StringComparison.Ordinal) && functions.Contains("'IF'", StringComparison.Ordinal) &&
          functions.Contains("'TF'", StringComparison.Ordinal) && functions.Contains("@schema", StringComparison.Ordinal), "metadata-functions");
    foreach (var sql in new[] { keys, indexes, functions })
        Check(!sql.Contains("sqlorynschema", StringComparison.Ordinal), "metadata-no-literal-schema");
    try { MetadataCatalog.ListKeys("d", ""); throw new Exception("FAIL metadata-keys-bad-schema"); }
    catch (ArgumentException) { }
    try { MetadataCatalog.ListIndexes("", "dbo"); throw new Exception("FAIL metadata-indexes-bad-db"); }
    catch (ArgumentException) { }
    Pass("metadata-keys-indexes-functions");

    var folders = ObjectExplorerService.SchemaFolders;
    foreach (var kind in new[] { "tables", "views", "keys", "indexes", "procedures", "functions" })
        Check(folders.Any(f => f.FolderKind == kind), "oe-folder-" + kind);
    Check(folders.Single(f => f.FolderKind == "keys").ChildKind == "key" &&
          folders.Single(f => f.FolderKind == "indexes").ChildKind == "index" &&
          folders.Single(f => f.FolderKind == "functions").ChildKind == "function", "oe-folder-child-kinds");
    Check(ObjectExplorerService.NodeKind.Key != ObjectExplorerService.NodeKind.Index &&
          ObjectExplorerService.NodeKind.Function != ObjectExplorerService.NodeKind.Procedure, "oe-node-kinds");
    Pass("oe-schema-folders");

    var raw = new RawRows(
        ["schema", "name", "parent", "type_desc", "is_unique"],
        ["nvarchar", "nvarchar", "nvarchar", "nvarchar", "bit"],
        [
            ["dbo", "PK_Orders", "Orders", "PRIMARY_KEY_CONSTRAINT", true],
            ["dbo", "FK_Orders_Cust", "Orders", "FOREIGN_KEY_CONSTRAINT", false],
            ["dbo", "IX_Orders_Date\nevil", "Orders", "NONCLUSTERED", true],
            ["dbo", "fnTotal", null, "SQL_SCALAR_FUNCTION", false],
        ],
        Truncated: true);
    var parsed = SchemaObjectCatalog.Parse(raw);
    Check(parsed.Count == 4 && parsed[0].Parent == "Orders" && parsed[0].IsUnique && !parsed[1].IsUnique && parsed[3].Parent is null, "schema-object-parse");
    Check(SchemaObjectCatalog.Title("key", parsed[0]) == "Orders.PK_Orders · primary key", "schema-object-title-pk");
    Check(SchemaObjectCatalog.Title("key", parsed[1]).EndsWith("foreign key", StringComparison.Ordinal), "schema-object-title-fk");
    var indexTitle = SchemaObjectCatalog.Title("index", parsed[2]);
    Check(!indexTitle.Contains('\n') && indexTitle.Contains("unique", StringComparison.Ordinal) && indexTitle.Contains("nonclustered", StringComparison.Ordinal), "schema-object-title-index-sanitised");
    Check(SchemaObjectCatalog.Title("function", parsed[3]) == "fnTotal · scalar", "schema-object-title-function");
    // Truncation honesty survives the new listings.
    var note = CatalogTruncation.Describe(parsed.Count, raw.Truncated, raw.ByteBudgetExhausted);
    Check(note is not null && note.Contains("truncated", StringComparison.OrdinalIgnoreCase), "schema-object-truncation-note");
    Pass("schema-object-catalog");
}

// Create table with primary key
{
    TableDesignDraft Draft(IReadOnlyList<string> pk, params ColumnDefinition[] cols) => new()
    {
        Database = "App", Schema = "dbo", TableName = "Orders", Columns = cols, PrimaryKeyColumns = pk
    };
    var id = new ColumnDefinition { Name = "Id", DataType = "int", IsNullable = false, IsIdentity = true };
    var tenant = new ColumnDefinition { Name = "Tenant", DataType = "int", IsNullable = false };
    var note = new ColumnDefinition { Name = "Note", DataType = "nvarchar", MaxLength = 50 };

    var single = TableDesignSql.PreviewCreate(Draft(["Id"], id, note)).ExecutableSql;
    Check(single.Contains("CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([Id])", StringComparison.Ordinal), "design-pk-single");
    Check(single.IndexOf("[Note]", StringComparison.Ordinal) < single.IndexOf("CONSTRAINT", StringComparison.Ordinal) &&
          single.Contains("[Note] nvarchar(50) NULL,\n", StringComparison.Ordinal), "design-pk-after-columns-with-comma");
    var composite = TableDesignSql.PreviewCreate(Draft(["Tenant", "Id"], id, tenant)).ExecutableSql;
    Check(composite.Contains("PRIMARY KEY CLUSTERED ([Tenant], [Id])", StringComparison.Ordinal), "design-pk-composite-order");
    var named = TableDesignSql.PreviewCreate((Draft(["Id"], id)) with { PrimaryKeyName = "PK_Custom]" }).ExecutableSql;
    Check(named.Contains("CONSTRAINT [PK_Custom]]]", StringComparison.Ordinal), "design-pk-named-quoted");
    var longName = TableDesignSql.PreviewCreate((Draft(["Id"], id)) with { TableName = new string('t', 128) }).ExecutableSql;
    Check(longName.Contains("CONSTRAINT [PK_" + new string('t', 125) + "]", StringComparison.Ordinal), "design-pk-default-name-truncated");
    Check(!TableDesignSql.PreviewCreate(Draft([], id, note)).ExecutableSql.Contains("CONSTRAINT", StringComparison.Ordinal), "design-no-pk-unchanged");

    void ExpectPkInvalid(TableDesignDraft d, string name)
    {
        try { TableDesignSql.PreviewCreate(d); throw new Exception("FAIL " + name); }
        catch (ArgumentException) { }
    }
    ExpectPkInvalid(Draft(["Missing"], id), "design-pk-unknown-column");
    ExpectPkInvalid(Draft(["Id", "id"], id), "design-pk-duplicate-column");
    ExpectPkInvalid(Draft(["Note"], id, note), "design-pk-nullable-column");
    ExpectPkInvalid(Draft(["Doc"], new ColumnDefinition { Name = "Doc", DataType = "xml", IsNullable = false }), "design-pk-xml-column");
    ExpectPkInvalid(Draft(["Doc"], new ColumnDefinition { Name = "Doc", DataType = "nvarchar", MaxLength = -1, IsNullable = false }), "design-pk-max-column");
    ExpectPkInvalid(Draft(["Doc"], new ColumnDefinition { Name = "Doc", DataType = "text", IsNullable = false }), "design-pk-text-column");
    ExpectPkInvalid(Draft(Enumerable.Range(0, 33).Select(i => "c" + i).ToList(),
        Enumerable.Range(0, 33).Select(i => new ColumnDefinition { Name = "c" + i, DataType = "int", IsNullable = false }).ToArray()), "design-pk-too-many-columns");
    ExpectPkInvalid((Draft(["Id"], id)) with { PrimaryKeyName = "" }, "design-pk-empty-name");

    // Hostile identifiers stay inside quoting.
    var hostile = TableDesignSql.PreviewCreate(new TableDesignDraft
    {
        Database = "A", Schema = "s", TableName = "t]\nDROP TABLE x;--",
        Columns = [new ColumnDefinition { Name = "i]\nDROP", DataType = "int", IsNullable = false }],
        PrimaryKeyColumns = ["i]\nDROP"]
    }).ExecutableSql;
    Check(hostile.Contains("PRIMARY KEY CLUSTERED ([i]]\nDROP])", StringComparison.Ordinal) &&
          hostile.Contains("CONSTRAINT [PK_t]]\nDROP TABLE x;--]", StringComparison.Ordinal), "design-pk-hostile-quoted");

    // Alter never silently rewrites a key.
    var existing = Draft(["Id"], id, note);
    Check(!TableDesignSql.PreviewAlter(existing, existing with { Columns = [id, note, new ColumnDefinition { Name = "N2", DataType = "int" }] }).ExecutableSql.Contains("PRIMARY", StringComparison.Ordinal), "design-alter-keeps-pk-untouched");
    try { TableDesignSql.PreviewAlter(existing, existing with { PrimaryKeyColumns = [] }); throw new Exception("FAIL design-alter-pk-change"); }
    catch (NotSupportedException) { }
    try { TableDesignSql.PreviewAlter(existing, existing with { Columns = [note] , PrimaryKeyColumns = ["Id"] }); throw new Exception("FAIL design-alter-drop-pk-column"); }
    catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { }
    Pass("table-design-primary-key");
}

// Estimated execution plans
{
    const string Insert = "INSERT dbo.Orders (Id) VALUES (1)";

    // Order: SET ON, then batch, then SET OFF; DML never executes.
    {
        var runner = new FakePlanRunner();
        var result = await EstimatedPlanService.EstimateAsync(runner, Insert);
        Check(result.Status == EstimatedPlanStatus.Succeeded && result.PlanXml.Count == 1, "estimate-plan-succeeds");
        Check(runner.Log.SequenceEqual(["SET ON", "BATCH " + Insert, "SET OFF"]), "estimate-plan-order-on-batch-off");
        Check(!runner.ShowplanOn && !result.SessionTainted, "estimate-plan-leaves-showplan-off");
        Check(runner.DmlExecutions == 0, "estimate-plan-insert-never-executed");
        Pass("estimate-plan-order-and-no-dml");
    }

    // SHOWPLAN permission denied: batch never delivered, nothing executed, clear error.
    {
        var runner = new FakePlanRunner { OnFailure = new PlanCommandException(262, "SHOWPLAN permission denied in database 'App'.") };
        var result = await EstimatedPlanService.EstimateAsync(runner, Insert);
        Check(result.Status == EstimatedPlanStatus.PermissionDenied && result.PlanXml.Count == 0, "estimate-plan-permission-status");
        Check(result.Error is not null && result.Error.Contains("SHOWPLAN", StringComparison.Ordinal) &&
              result.Error.Contains("not executed", StringComparison.OrdinalIgnoreCase), "estimate-plan-permission-message");
        Check(runner.BatchCalls == 0 && runner.DmlExecutions == 0, "estimate-plan-permission-no-batch-no-dml");
        Check(!runner.ShowplanOn, "estimate-plan-permission-showplan-off");
        Pass("estimate-plan-permission-denied-no-execution");
    }

    // Any failure of SET ON (not only permission) means the batch is never sent.
    {
        var runner = new FakePlanRunner { OnFailure = new PlanCommandException(null, "connection reset") };
        var result = await EstimatedPlanService.EstimateAsync(runner, Insert);
        Check(result.Status == EstimatedPlanStatus.Failed && runner.BatchCalls == 0 && runner.DmlExecutions == 0, "estimate-plan-on-failure-no-batch");
        Pass("estimate-plan-set-on-failure-no-execution");
    }

    // Compile error in the batch: SET OFF still sent; session clean.
    {
        var runner = new FakePlanRunner { BatchFailure = new PlanCommandException(208, "Invalid object name 'dbo.Nope'.") };
        var result = await EstimatedPlanService.EstimateAsync(runner, "SELECT * FROM dbo.Nope");
        Check(result.Status == EstimatedPlanStatus.Failed && result.Error!.Contains("Invalid object name", StringComparison.Ordinal), "estimate-plan-compile-error-status");
        Check(runner.Log[^1] == "SET OFF" && !runner.ShowplanOn && !result.SessionTainted && runner.DmlExecutions == 0, "estimate-plan-compile-error-turns-off");
        Pass("estimate-plan-batch-error-turns-off");
    }

    // SET OFF failing taints the session so the caller can drop the connection.
    {
        var runner = new FakePlanRunner { OffFailure = new PlanCommandException(null, "network down") };
        var result = await EstimatedPlanService.EstimateAsync(runner, Insert);
        Check(result.SessionTainted && result.Error is not null && result.Error.Contains("SHOWPLAN", StringComparison.Ordinal), "estimate-plan-off-failure-tainted");
        Check(runner.DmlExecutions == 0, "estimate-plan-off-failure-no-dml");
        Pass("estimate-plan-off-failure-taints-session");

        // Verifier S-1: unexpected non-PlanCommandException must still attempt OFF and taint the session.
        {
            var unexpected = new FakePlanRunner { BatchFailure = new InvalidOperationException("reader exploded") };
            var r = await EstimatedPlanService.EstimateAsync(unexpected, "INSERT INTO t VALUES (1);");
            Check(r.Status == EstimatedPlanStatus.Failed && r.SessionTainted, "estimate-unexpected-taints");
            Check(unexpected.Log.Contains("SET ON") && unexpected.Log.Contains("SET OFF"), "estimate-unexpected-still-turns-off");
            Check(unexpected.DmlExecutions == 0 && !unexpected.ShowplanOn, "estimate-unexpected-no-dml-showplan-off");
            Pass("estimate-plan-unexpected-exception-turns-off");
        }
    }

    // Cancellation before anything is sent: no server traffic at all.
    {
        var runner = new FakePlanRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await EstimatedPlanService.EstimateAsync(runner, Insert, cts.Token);
        Check(result.Status == EstimatedPlanStatus.Cancelled && runner.Log.Count == 0 && runner.DmlExecutions == 0, "estimate-plan-precancel-sends-nothing");
    }
    // Cancellation while the batch is compiling: SET OFF still sent, status Cancelled.
    {
        var runner = new FakePlanRunner { BatchFailure = new OperationCanceledException() };
        var result = await EstimatedPlanService.EstimateAsync(runner, Insert);
        Check(result.Status == EstimatedPlanStatus.Cancelled && runner.DmlExecutions == 0 && !runner.ShowplanOn, "estimate-plan-cancel");
        Check(runner.Log[^1] == "SET OFF" && !result.SessionTainted, "estimate-plan-cancel-turns-off");
        Pass("estimate-plan-cancel");
    }

    // User SQL cannot switch SHOWPLAN off (or otherwise defeat it): rejected before anything is sent.
    foreach (var bypass in new[]
    {
        "SET SHOWPLAN_XML OFF; " + Insert,
        "set  showplan_all off\n" + Insert,
        "SET NOEXEC OFF;\n" + Insert,
        "SET STATISTICS XML ON; " + Insert,
        "SET PARSEONLY OFF; " + Insert,
        "SET FMTONLY OFF; " + Insert,
        "/* hi */ SET\tSHOWPLAN_TEXT OFF " + Insert,
    })
    {
        var runner = new FakePlanRunner();
        var result = await EstimatedPlanService.EstimateAsync(runner, bypass);
        Check(result.Status == EstimatedPlanStatus.Rejected && runner.Log.Count == 0 && runner.DmlExecutions == 0, "estimate-plan-rejects-bypass:" + bypass);
    }
    // Look-alikes inside comments, strings and identifiers are fine.
    foreach (var benign in new[]
    {
        "SELECT 'SET SHOWPLAN_XML OFF' AS s",
        "-- SET SHOWPLAN_XML OFF\nSELECT 1",
        "/* SET NOEXEC OFF */ SELECT 1",
        "SELECT [SET SHOWPLAN_XML OFF] FROM t",
        "SET NOCOUNT ON; SELECT 1",
    })
    {
        var runner = new FakePlanRunner();
        var result = await EstimatedPlanService.EstimateAsync(runner, benign);
        Check(result.Status == EstimatedPlanStatus.Succeeded && runner.DmlExecutions == 0, "estimate-plan-allows-lookalike:" + benign);
    }
    Pass("estimate-plan-rejects-showplan-bypass");

    // GO scripts: one SET ON ... one SET OFF, each batch estimated once; SQLCMD scripts rejected untouched.
    {
        var runner = new FakePlanRunner();
        var result = await EstimatedPlanService.EstimateAsync(runner, "SELECT 1\nGO 5\nINSERT dbo.t VALUES (1)\nGO\nDELETE dbo.t");
        Check(result.Status == EstimatedPlanStatus.Succeeded && result.PlanXml.Count == 3 && runner.BatchCalls == 3, "estimate-plan-go-batches-once");
        Check(runner.Log.Count(l => l == "SET ON") == 1 && runner.Log.Count(l => l == "SET OFF") == 1 && runner.Log[0] == "SET ON" && runner.Log[^1] == "SET OFF", "estimate-plan-go-single-on-off");
        Check(runner.DmlExecutions == 0, "estimate-plan-go-no-dml");

        var rejected = new FakePlanRunner();
        var r2 = await EstimatedPlanService.EstimateAsync(rejected, "INSERT dbo.t VALUES (1)\nGO\n:r evil.sql");
        Check(r2.Status == EstimatedPlanStatus.Rejected && rejected.Log.Count == 0, "estimate-plan-sqlcmd-rejected");
        var empty = await EstimatedPlanService.EstimateAsync(new FakePlanRunner(), "   ");
        Check(empty.Status == EstimatedPlanStatus.Rejected, "estimate-plan-empty-rejected");
        Pass("estimate-plan-go-and-preflight");
    }

    // A failing later batch stops and still turns SHOWPLAN off.
    {
        var runner = new FakePlanRunner();
        var calls = 0;
        runner.PlanFor = _ => ++calls == 2 ? throw new PlanCommandException(102, "Incorrect syntax") : ["<p/>"];
        var result = await EstimatedPlanService.EstimateAsync(runner, "SELECT 1\nGO\nSELEC 2\nGO\nSELECT 3");
        Check(result.Status == EstimatedPlanStatus.Failed && runner.BatchCalls == 2 && result.PlanXml.Count == 1 && runner.Log[^1] == "SET OFF", "estimate-plan-later-batch-failure");
        Pass("estimate-plan-later-batch-failure");
    }

    // Oversized plan text is omitted, not truncated silently.
    {
        var runner = new FakePlanRunner { PlanFor = _ => [new string('x', EstimatedPlanService.MaxPlanCharacters + 1)] };
        var result = await EstimatedPlanService.EstimateAsync(runner, "SELECT 1");
        Check(result.Status == EstimatedPlanStatus.Failed && result.Error!.Contains("too large", StringComparison.OrdinalIgnoreCase), "estimate-plan-oversize");
        Pass("estimate-plan-oversize");
    }

    // Per-plan limit applies via the fake's oversized-plan support too, and the plan is never retained truncated.
    {
        var runner = new FakePlanRunner { OversizedPlanCharacters = EstimatedPlanService.MaxPlanCharacters + 1 };
        var result = await EstimatedPlanService.EstimateAsync(runner, "SELECT 1");
        Check(result.Status == EstimatedPlanStatus.Failed && result.PlanXml.Count == 0 && runner.Log[^1] == "SET OFF" && !result.SessionTainted, "estimate-plan-oversize-fake");
        Pass("estimate-plan-oversize-fake");
    }

    // Total budget across every plan and batch: stops before the next batch, never truncates.
    {
        const int threeMiB = 3 * 1024 * 1024;
        Check(EstimatedPlanService.MaxTotalPlanCharacters >= EstimatedPlanService.MaxPlanCharacters &&
              threeMiB < EstimatedPlanService.MaxPlanCharacters &&
              3 * threeMiB > EstimatedPlanService.MaxTotalPlanCharacters &&
              2 * threeMiB <= EstimatedPlanService.MaxTotalPlanCharacters, "estimate-plan-total-budget-constants");
        var runner = new FakePlanRunner { OversizedPlanCharacters = threeMiB };
        var result = await EstimatedPlanService.EstimateAsync(runner, "SELECT 1\nGO\nSELECT 2\nGO\nSELECT 3\nGO\nSELECT 4");
        Check(result.Status == EstimatedPlanStatus.Failed && runner.BatchCalls == 3 && result.PlanXml.Count == 2 &&
              result.PlanXml.All(p => p.Length == threeMiB) &&
              result.Error!.Contains("total", StringComparison.OrdinalIgnoreCase) && runner.Log[^1] == "SET OFF" &&
              !runner.ShowplanOn && !result.SessionTainted && runner.DmlExecutions == 0, "estimate-plan-total-budget-stops");

        // Many plans within a single batch also count.
        var multi = new FakePlanRunner { PlanFor = _ => [new string('y', threeMiB), new string('y', threeMiB), new string('y', threeMiB)] };
        var r2 = await EstimatedPlanService.EstimateAsync(multi, "SELECT 1");
        Check(r2.Status == EstimatedPlanStatus.Failed && r2.PlanXml.Count == 2 && r2.PlanXml.All(p => p.Length == threeMiB), "estimate-plan-total-budget-single-batch");

        // Exactly at the budget is allowed.
        var half = EstimatedPlanService.MaxTotalPlanCharacters / 2;
        var exact = new FakePlanRunner { OversizedPlanCharacters = half };
        var r3 = await EstimatedPlanService.EstimateAsync(exact, "SELECT 1\nGO\nSELECT 2");
        Check(half <= EstimatedPlanService.MaxPlanCharacters && r3.Status == EstimatedPlanStatus.Succeeded && r3.PlanXml.Count == 2, "estimate-plan-total-budget-exact");
        Pass("estimate-plan-total-budget");
    }

    // Total-budget failure with a failing OFF taints the session.
    {
        var runner = new FakePlanRunner
        {
            OversizedPlanCharacters = 3 * 1024 * 1024,
            OffFailure = new PlanCommandException(null, "network down")
        };
        var result = await EstimatedPlanService.EstimateAsync(runner, "SELECT 1\nGO\nSELECT 2\nGO\nSELECT 3\nGO\nSELECT 4");
        Check(result.Status == EstimatedPlanStatus.Failed && result.SessionTainted && runner.BatchCalls == 3, "estimate-plan-total-budget-off-failure-tainted");
        Pass("estimate-plan-total-budget-off-failure");
    }

    // Cancel mid-estimate: Cancelled, OFF attempted; when OFF fails the session is tainted.
    {
        var runner = new FakePlanRunner
        {
            BatchFailure = new OperationCanceledException(),
            OffFailure = new PlanCommandException(null, "network down")
        };
        var result = await EstimatedPlanService.EstimateAsync(runner, Insert);
        Check(result.Status == EstimatedPlanStatus.Cancelled && result.SessionTainted && runner.Log[^1] == "SET OFF" && runner.DmlExecutions == 0, "estimate-plan-cancel-off-failure-tainted");

        // Cancellation token fired while the batch is running (runner honours the token).
        using var cts = new CancellationTokenSource();
        var ctsRunner = new FakePlanRunner { PlanFor = _ => { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return ["<p/>"]; } };
        var r2 = await EstimatedPlanService.EstimateAsync(ctsRunner, Insert, cts.Token);
        Check(r2.Status == EstimatedPlanStatus.Cancelled && !r2.SessionTainted && ctsRunner.Log[^1] == "SET OFF" && !ctsRunner.ShowplanOn, "estimate-plan-cancel-mid-estimate-off-attempted");
        Pass("estimate-plan-cancel-taint");
    }

    // Unexpected exception types while turning SHOWPLAN off also taint (never leak out unclassified).
    foreach (Exception offFailure in new Exception[]
    {
        new InvalidOperationException("reset boom"),
        new ObjectDisposedException("connection"),
        new NullReferenceException("reset boom"),
        new IOException("pipe broke"),
    })
    {
        var runner = new FakePlanRunner { OffFailure = offFailure };
        var result = await EstimatedPlanService.EstimateAsync(runner, Insert);
        Check(result.SessionTainted && result.Status == EstimatedPlanStatus.Failed, "estimate-plan-unexpected-off-exception-tainted:" + offFailure.GetType().Name);
        var cancelled = new FakePlanRunner { OffFailure = offFailure, BatchFailure = new OperationCanceledException() };
        var r2 = await EstimatedPlanService.EstimateAsync(cancelled, Insert);
        Check(r2.SessionTainted && r2.Status == EstimatedPlanStatus.Cancelled, "estimate-plan-cancel-unexpected-off-tainted:" + offFailure.GetType().Name);
    }
    Pass("estimate-plan-unexpected-off-exception");

    // Provider-bound plan reader: sequential chunked reads, hard cap, no silent truncation.
    {
        static (Func<long, char[], int, int> GetChars, Func<long> Served) Source(string text)
        {
            long served = 0;
            return ((offset, buffer, length) =>
            {
                var n = (int)Math.Min(length, Math.Max(0, text.Length - offset));
                text.CopyTo((int)offset, buffer, 0, n);
                served += n;
                return n;
            }, () => served);
        }

        var small = "<ShowPlanXML>" + new string('a', 30_000) + "</ShowPlanXML>";
        var (smallGet, _) = Source(small);
        Check(PlanXmlReader.ReadBounded(smallGet, 100_000, CancellationToken.None) == small, "plan-reader-under-budget");

        var (exactGet, _) = Source(new string('b', 20_000));
        Check(PlanXmlReader.ReadBounded(exactGet, 20_000, CancellationToken.None).Length == 20_000, "plan-reader-exactly-budget");

        var (bigGet, served) = Source(new string('c', 10_000_000));
        try
        {
            PlanXmlReader.ReadBounded(bigGet, 50_000, CancellationToken.None);
            throw new Exception("FAIL plan-reader-over-budget did not throw");
        }
        catch (PlanCommandException ex)
        {
            Check(ex.Message.Contains("too large", StringComparison.OrdinalIgnoreCase), "plan-reader-over-budget-message");
        }
        Check(served() < 50_000 + 2 * PlanXmlReader.ChunkCharacters, "plan-reader-stops-reading-at-budget:" + served());

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var (cGet, _) = Source("<a/>");
        try { PlanXmlReader.ReadBounded(cGet, 100, cancelled.Token); throw new Exception("FAIL plan-reader-cancel"); }
        catch (OperationCanceledException) { }
        Pass("plan-reader-bounded");
    }

    // Messages formatting is single-purpose text and says nothing ran.
    {
        var ok = new EstimatedPlanResult(EstimatedPlanStatus.Succeeded, ["<ShowPlanXML/>"], null, false);
        var text = EstimatedPlanService.FormatForMessages(ok);
        Check(text.Contains("<ShowPlanXML/>", StringComparison.Ordinal) && text.Contains("not executed", StringComparison.OrdinalIgnoreCase), "estimate-plan-format-success");
        var bad = EstimatedPlanService.FormatForMessages(new EstimatedPlanResult(EstimatedPlanStatus.PermissionDenied, [], "no SHOWPLAN\nline2", false));
        Check(bad.Contains("not executed", StringComparison.OrdinalIgnoreCase) && bad.Contains("no SHOWPLAN", StringComparison.Ordinal), "estimate-plan-format-error");
        Pass("estimate-plan-format");
    }

    // SqlSession seam: the estimate path never reaches ExecuteAsync's batch executor.
    {
        var executed = 0;
        var runner = new FakePlanRunner();
        await using var session = new SqlSession
        {
            BatchExecutorForTests = (_, _, _) => { executed++; throw new Exception("FAIL estimate must not execute"); },
            PlanRunnerForTests = () => runner
        };
        var result = await session.EstimatePlanAsync(Insert);
        Check(result.Status == EstimatedPlanStatus.Succeeded && executed == 0 && runner.DmlExecutions == 0, "sqlsession-estimate-no-execute");

        var denied = new FakePlanRunner { OnFailure = new PlanCommandException(262, "denied") };
        await using var session2 = new SqlSession
        {
            BatchExecutorForTests = (_, _, _) => { executed++; throw new Exception("FAIL estimate must not execute"); },
            PlanRunnerForTests = () => denied
        };
        var r2 = await session2.EstimatePlanAsync(Insert);
        Check(r2.Status == EstimatedPlanStatus.PermissionDenied && executed == 0 && denied.BatchCalls == 0 && !session2.HasActiveCommand, "sqlsession-estimate-denied-no-execute");

        // Without a connection (and no seam) the call fails closed instead of running anything.
        await using var offline = new SqlSession();
        try { await offline.EstimatePlanAsync(Insert); throw new Exception("FAIL estimate-offline"); }
        catch (InvalidOperationException) { }
        Pass("sqlsession-estimate-plan");
    }

    // Tainted estimate: session is target-unknown; ExecuteAsync/EstimatePlanAsync refuse until reconnect.
    foreach (var taint in new Func<FakePlanRunner>[]
    {
        () => new FakePlanRunner { OffFailure = new PlanCommandException(null, "network down") },
        () => new FakePlanRunner { OffFailure = new NullReferenceException("reset boom") },
        () => new FakePlanRunner { BatchFailure = new InvalidOperationException("reader exploded") },
        () => new FakePlanRunner { OversizedPlanCharacters = 3 * 1024 * 1024, OffFailure = new PlanCommandException(null, "network down") },
    })
    {
        var executed = 0;
        var runner = taint();
        var runnerCreations = 0;
        await using var session = new SqlSession
        {
            BatchExecutorForTests = (_, _, _) => { executed++; throw new Exception("FAIL tainted session must not execute"); },
            PlanRunnerForTests = () => { runnerCreations++; return runner; }
        };
        var sql = runner.OversizedPlanCharacters is null ? Insert : "SELECT 1\nGO\nSELECT 2\nGO\nSELECT 3\nGO\nSELECT 4";
        var result = await session.EstimatePlanAsync(sql);
        Check(result.SessionTainted && session.TargetUnknown && !session.HasActiveCommand, "sqlsession-estimate-tainted-target-unknown");

        try { await session.ExecuteAsync("SELECT 1"); throw new Exception("FAIL execute after tainted estimate"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("target is unknown", StringComparison.OrdinalIgnoreCase), "sqlsession-tainted-execute-message"); }
        try { await session.ExecuteAsync("SELECT 1\nGO\nSELECT 2"); throw new Exception("FAIL multi-batch execute after tainted estimate"); }
        catch (InvalidOperationException) { }
        try { await session.EstimatePlanAsync(Insert); throw new Exception("FAIL estimate after tainted estimate"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("target is unknown", StringComparison.OrdinalIgnoreCase), "sqlsession-tainted-estimate-message"); }
        Check(executed == 0 && runnerCreations == 1 && runner.DmlExecutions == 0, "sqlsession-tainted-refuses-everything");

        // Disconnect alone must not clear the taint; recovery is ConnectAsync only.
        await session.DisconnectAsync();
        Check(session.TargetUnknown, "sqlsession-tainted-survives-disconnect");
        try { await session.EstimatePlanAsync(Insert); throw new Exception("FAIL estimate after disconnect while tainted"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("target is unknown", StringComparison.OrdinalIgnoreCase), "sqlsession-tainted-after-disconnect-message"); }
        Check(runnerCreations == 1 && executed == 0, "sqlsession-tainted-disconnect-still-refuses");
    }
    Pass("sqlsession-estimate-tainted-refuses-until-reconnect");

    // A clean estimate does not poison the session.
    {
        await using var session = new SqlSession
        {
            PlanRunnerForTests = () => new FakePlanRunner()
        };
        var result = await session.EstimatePlanAsync(Insert);
        Check(result.Status == EstimatedPlanStatus.Succeeded && !result.SessionTainted && !session.TargetUnknown, "sqlsession-clean-estimate-not-tainted");
        Pass("sqlsession-clean-estimate-not-tainted");
    }
}

// Large-script responsiveness (generous bound; no UI). Elapsed time is reported in the failure message only.
{
    const int Bound = 2000;
    var line = "SELECT 1 AS [col]; -- padding padding padding padding padding\n";
    var sb = new System.Text.StringBuilder(2_200_000);
    while (sb.Length < 2_000_000)
        sb.Append(line);
    sb.Append("-- NEEDLE_AT_END\n");
    var big = sb.ToString();
    Check(big.Length >= 2_000_000, "large-script-size");

    var bigDoc = new ScriptDocument();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    bigDoc.SetText(big);
    var setMs = sw.ElapsedMilliseconds;
    if (setMs > Bound || bigDoc.Text.Length != big.Length || !bigDoc.IsDirty)
        throw new Exception($"FAIL large-script-settext: {setMs} ms (bound {Bound} ms), length {bigDoc.Text.Length}");

    sw.Restart();
    var hit = FindReplaceEngine.FindNext(bigDoc.Text, "NEEDLE_AT_END", 0, caseSensitive: true);
    var findMs = sw.ElapsedMilliseconds;
    if (findMs > Bound || hit != big.Length - "-- NEEDLE_AT_END\n".Length + 3)
        throw new Exception($"FAIL large-script-findnext: {findMs} ms (bound {Bound} ms), index {hit}");

    sw.Restart();
    var miss = FindReplaceEngine.FindNext(bigDoc.Text, "ABSENT_NEEDLE", bigDoc.Text.Length / 2, caseSensitive: false);
    var missMs = sw.ElapsedMilliseconds;
    if (missMs > Bound || miss != -1)
        throw new Exception($"FAIL large-script-findnext-miss-wrap: {missMs} ms (bound {Bound} ms), index {miss}");
    Pass("large-script-settext-and-find");
}

// Offline keyboard/automation contract: critical controls keep stable names and shortcuts (source scan; not a UI run).
{
    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SQLoryn.slnx")))
                return d.FullName;
        throw new Exception("FAIL repo root (SQLoryn.slnx) not found from test base directory");
    }

    var appDir = Path.Combine(FindRepoRoot(), "src", "Sqloryn.App");
    const string Automation = "AutomationProperties.Name";

    static Dictionary<string, string?> NamedControls(string axamlPath)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var el in System.Xml.Linq.XDocument.Load(axamlPath).Descendants())
        {
            var n = el.Attribute("Name")?.Value;
            if (n is not null)
                map[n] = el.Attribute("AutomationProperties.Name")?.Value;
        }
        return map;
    }

    // Feature-local views (WelcomeView) keep the same stable names; scan them with the window.
    var main = NamedControls(Path.Combine(appDir, "MainWindow.axaml"));
    foreach (var (k, v) in NamedControls(Path.Combine(appDir, "WelcomeView.axaml")))
        main[k] = v;
    var expectMain = new Dictionary<string, string>
    {
        ["WelcomeConnect"] = "New connection",
        ["WelcomeNewQuery"] = "New query",
        ["WelcomeOpen"] = "Open SQL file",
        ["ConnectButton"] = "Connect",
        ["RunButton"] = "Run query",
        ["CancelButton"] = "Cancel query",
        ["SidebarToggle"] = "Toggle object explorer sidebar"
    };
    foreach (var (control, name) in expectMain)
        Check(main.TryGetValue(control, out var actual) && actual == name, $"a11y-main-{control}-has-automation-name");

    var connectXml = System.Xml.Linq.XDocument.Load(Path.Combine(appDir, "ConnectWindow.axaml"));
    var connectButtons = connectXml.Descendants().Where(e => e.Name.LocalName == "Button")
        .ToDictionary(e => e.Attribute("Content")?.Value ?? "", e => e.Attribute(Automation)?.Value);
    Check(connectButtons.GetValueOrDefault("Connect") == "Connect" && connectButtons.GetValueOrDefault("Cancel") == "Cancel connect", "a11y-connect-dialog-buttons");
    var defaultConnect = connectXml.Descendants().Any(e => e.Name.LocalName == "Button" && e.Attribute("Content")?.Value == "Connect" && e.Attribute("IsDefault")?.Value == "True");
    Check(defaultConnect, "a11y-connect-dialog-return-key-default");

    var shell = File.ReadAllText(Path.Combine(appDir, "MainWindow.Shell.cs"));
    Check(shell.Contains("Key.F5", StringComparison.Ordinal) && shell.Contains("Key.Escape", StringComparison.Ordinal), "keyboard-f5-run-escape-cancel-present");
    Check(shell.Contains("Key.Enter, KeyModifiers.Meta", StringComparison.Ordinal), "keyboard-mac-cmd-return-run-present");

    // App Save path: external-change refusal is caught before the generic handler so the buffer stays dirty with a distinct message.
    var mainCode = File.ReadAllText(Path.Combine(appDir, "MainWindow.axaml.cs"));
    var externalCatch = mainCode.IndexOf("catch (ExternalDocumentChangedException", StringComparison.Ordinal);
    var genericCatch = mainCode.IndexOf("catch (Exception ex)", externalCatch < 0 ? 0 : externalCatch, StringComparison.Ordinal);
    Check(externalCatch >= 0 && genericCatch > externalCatch, "app-save-external-change-handler-precedes-generic");
    var externalException = new ExternalDocumentChangedException("/tmp/x.sql");
    Check(externalException.Message.Length > 0 && !externalException.Message.Contains("secret", StringComparison.OrdinalIgnoreCase), "app-save-external-change-message-present");
    Pass("a11y-keyboard-contract-source-scan");

    // Visual system: semantic resources exist in both theme variants; views use no legacy Sq.* keys.
    var appXaml = File.ReadAllText(Path.Combine(appDir, "App.axaml"));
    foreach (var key in new[] { "WindowBg", "ContentBg", "SidebarBg", "Separator", "Selection", "Focus", "Accent", "Success", "Warning", "Destructive", "SecondaryText" })
        Check(System.Text.RegularExpressions.Regex.Matches(appXaml, $"x:Key=\"{key}\"").Count == 2, $"visual-semantic-resource-{key}-in-light-and-dark");
    Check(Directory.GetFiles(appDir, "*.axaml").All(f => !File.ReadAllText(f).Contains("Sq.", StringComparison.Ordinal)), "visual-no-legacy-Sq-keys");
    Check(appXaml.Contains("RequestedThemeVariant=\"Default\"", StringComparison.Ordinal), "visual-system-theme-tracking-default");
    Pass("visual-system-source-scan");

    // Settings: every control is either applied or documented read-only; the rows cap text is derived from the engine cap.
    var settingsXaml = File.ReadAllText(Path.Combine(appDir, "SettingsWindow.axaml"));
    foreach (var section in new[] { "General", "Editor", "Results", "Connections", "Privacy" })
        Check(settingsXaml.Contains($"Header=\"{section}\"", StringComparison.Ordinal), $"settings-section-{section}");
    var settingsCode = File.ReadAllText(Path.Combine(appDir, "SettingsWindow.axaml.cs"));
    Check(settingsCode.Contains("AppSettings.MaxResultsMaxRows", StringComparison.Ordinal) && !settingsXaml.Contains("5,000", StringComparison.Ordinal), "settings-retention-text-derived-from-cap");
    Pass("settings-grouping-source-scan");
}

// WS5-T1: transactional designer apply + schema freshness (fakes only; live SQL is separate acceptance)
{
    var ordersTable = new TableRef("App", "dbo", "Orders");
    TableDesignStatement S(string sql) => new(sql);
    var s1 = S("ALTER TABLE [App].[dbo].[Orders] ADD [A] int NULL;\n");
    var s2 = S("ALTER TABLE [App].[dbo].[Orders] ADD [B] int NULL;\n");
    var s3 = S("ALTER TABLE [App].[dbo].[Orders] DROP COLUMN [C];\n");

    // success commits, in order, inside begin ... commit, after the freshness read
    var ok = new FakeSchemaTransactionFactory();
    var okResult = await TableDesignApply.ApplyAsync(ok, ordersTable, [s1, s2, s3], "fp-1");
    Check(okResult.Outcome == TableDesignApplyOutcome.Committed && okResult.Committed && okResult.StatementsExecuted == 3, "design-apply-commits");
    Check(string.Join(",", ok.Log) == "begin,fingerprint,exec,exec,exec,commit,dispose", "design-apply-order:" + string.Join(",", ok.Log));
    Check(ok.Durable.Count == 3 && ok.Durable[0] == s1.Sql && ok.Durable[2] == s3.Sql, "design-apply-durable-after-commit");

    // failure on statement 2 rolls everything back: no partial schema, statement 3 never runs
    var boom = new FakeSchemaTransactionFactory(null, new InvalidOperationException("synthetic ALTER failure"), null);
    var boomResult = await TableDesignApply.ApplyAsync(boom, ordersTable, [s1, s2, s3], "fp-1");
    Check(boomResult.Outcome == TableDesignApplyOutcome.RolledBackError && !boomResult.Committed, "design-apply-failure-rolls-back");
    Check(boomResult.FailedIndex == 1 && boomResult.StatementsExecuted == 1, "design-apply-failed-index");
    Check(boom.Log.Contains("rollback") && !boom.Log.Contains("commit") && boom.Durable.Count == 0, "design-apply-no-partial");
    Check(boom.Log.Count(l => l == "exec") == 2, "design-apply-stops-after-failure");
    Check(boomResult.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase) && !boomResult.RequiresReload, "design-apply-failure-message");

    // freshness mismatch refuses before any statement runs
    var stale = new FakeSchemaTransactionFactory { Fingerprint = "fp-CHANGED" };
    var staleResult = await TableDesignApply.ApplyAsync(stale, ordersTable, [s1], "fp-1");
    Check(staleResult.Outcome == TableDesignApplyOutcome.RefusedStale && !staleResult.Committed, "design-apply-stale-refused");
    Check(!stale.Log.Contains("exec") && !stale.Log.Contains("commit") && stale.Log.Contains("rollback"), "design-apply-stale-no-exec");
    Check(staleResult.Message.Contains("changed", StringComparison.OrdinalIgnoreCase), "design-apply-stale-message");

    // fingerprint read failure rolls back, nothing executed
    var fpFail = new FakeSchemaTransactionFactory { FingerprintFails = new InvalidOperationException("no catalog") };
    var fpFailResult = await TableDesignApply.ApplyAsync(fpFail, ordersTable, [s1], "fp-1");
    Check(fpFailResult.Outcome == TableDesignApplyOutcome.RolledBackError && !fpFail.Log.Contains("exec") && fpFail.Log.Contains("rollback"), "design-apply-fingerprint-error");

    // mixed or unrecognised batches are refused without opening a transaction
    var mixed = new FakeSchemaTransactionFactory();
    var mixedResult = await TableDesignApply.ApplyAsync(mixed, ordersTable, [s1, new TableDesignStatement("DROP DATABASE [App];", false)], "fp-1");
    Check(mixedResult.Outcome == TableDesignApplyOutcome.RefusedNonTransactional && mixed.Log.Count == 0, "design-apply-refuses-mixed");
    var notDdl = new FakeSchemaTransactionFactory();
    var notDdlResult = await TableDesignApply.ApplyAsync(notDdl, ordersTable, [new TableDesignStatement("DELETE FROM [App].[dbo].[Orders];", true)], "fp-1");
    Check(notDdlResult.Outcome == TableDesignApplyOutcome.RefusedNonTransactional && notDdl.Log.Count == 0, "design-apply-refuses-unrecognised-statement");
    var goBatch = new FakeSchemaTransactionFactory();
    var goResult = await TableDesignApply.ApplyAsync(goBatch, ordersTable, [S("ALTER TABLE [App].[dbo].[Orders] ADD [A] int NULL;\nGO\nDROP TABLE [x];")], "fp-1");
    Check(goResult.Outcome == TableDesignApplyOutcome.RefusedNonTransactional && goBatch.Log.Count == 0, "design-apply-refuses-go-batch");

    // argument validation
    foreach (var (label, act) in new (string, Func<Task>)[]
    {
        ("empty", () => TableDesignApply.ApplyAsync(new FakeSchemaTransactionFactory(), ordersTable, [], "fp-1")),
        ("no-fingerprint", () => TableDesignApply.ApplyAsync(new FakeSchemaTransactionFactory(), ordersTable, [s1], " ")),
    })
    {
        try { await act(); throw new Exception("FAIL design-apply-args-" + label); }
        catch (ArgumentException) { }
    }

    // begin failure: nothing written; rollback failure and commit failure never claim a clean state
    var beginFail = new FakeSchemaTransactionFactory { BeginFails = new InvalidOperationException("no connection") };
    var beginFailResult = await TableDesignApply.ApplyAsync(beginFail, ordersTable, [s1], "fp-1");
    Check(beginFailResult.Outcome == TableDesignApplyOutcome.RolledBackError && beginFailResult.StatementsExecuted == 0, "design-apply-begin-failure");
    var rbFail = new FakeSchemaTransactionFactory(new InvalidOperationException("x")) { RollbackFails = new InvalidOperationException("link down") };
    var rbFailResult = await TableDesignApply.ApplyAsync(rbFail, ordersTable, [s1], "fp-1");
    Check(rbFailResult.Outcome == TableDesignApplyOutcome.OutcomeUnknown && rbFailResult.RequiresReload, "design-apply-rollback-failure-unknown");
    var commitFail = new FakeSchemaTransactionFactory { CommitFails = new InvalidOperationException("link down") };
    var commitFailResult = await TableDesignApply.ApplyAsync(commitFail, ordersTable, [s1], "fp-1");
    Check(commitFailResult.Outcome == TableDesignApplyOutcome.OutcomeUnknown && !commitFailResult.Committed
          && !commitFailResult.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase), "design-apply-commit-failure-unknown");
    var disposeFail = new FakeSchemaTransactionFactory { DisposeFails = new InvalidOperationException("dispose") };
    var disposeFailResult = await TableDesignApply.ApplyAsync(disposeFail, ordersTable, [s1], "fp-1");
    Check(disposeFailResult.Outcome == TableDesignApplyOutcome.OutcomeUnknown, "design-apply-dispose-failure-after-commit");

    // cancellation mid-way still rolls back
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var cancelled = new FakeSchemaTransactionFactory();
    var cancelledResult = await TableDesignApply.ApplyAsync(cancelled, ordersTable, [s1, s2], "fp-1", cts.Token);
    Check(cancelledResult.Outcome == TableDesignApplyOutcome.RolledBackError && cancelled.Log.Contains("rollback") && !cancelled.Log.Contains("commit"), "design-apply-cancel-rolls-back");

    // the generators now expose per-statement transactional DDL that Apply accepts
    var id = new ColumnDefinition { Name = "Id", DataType = "int", IsNullable = false };
    var note = new ColumnDefinition { Name = "Note", DataType = "nvarchar(20)" };
    var existingDraft = new TableDesignDraft { Database = "App", Schema = "dbo", TableName = "Orders", Columns = [id, note], PrimaryKeyColumns = ["Id"] };
    var desiredDraft = existingDraft with { Columns = [id, new ColumnDefinition { Name = "Qty", DataType = "int" }] };
    var alter = TableDesignSql.PreviewAlter(existingDraft, desiredDraft);
    Check(alter.Statements.Count == 2 && alter.Statements.All(s => s.IsTransactional)
          && string.Concat(alter.Statements.Select(s => s.Sql)) == alter.ExecutableSql, "design-preview-statements-match-executable");
    var create = TableDesignSql.PreviewCreate(existingDraft);
    Check(create.Statements.Count == 1 && create.Statements[0].Sql == create.ExecutableSql, "design-create-statement-single");
    var viaGenerator = new FakeSchemaTransactionFactory();
    Check((await TableDesignApply.ApplyAsync(viaGenerator, ordersTable, alter.Statements, "fp-1")).Committed, "design-apply-generated-statements");

    // fingerprint: stable, sensitive to column definition, absent ordersTable recognised
    var header = new[] { "modified", "column_id", "name", "type_name", "max_length", "precision", "scale", "is_nullable", "is_identity", "is_computed", "pk_ordinal" };
    var types = header.Select(_ => "x").ToArray();
    RawRows Rows(params object?[][] rows) => new(header, types, rows, false);
    object?[] Col(int id, string name, string type, bool nullable = true, string modified = "2026-01-01T00:00:00.000")
        => [modified, id, name, type, (short)4, (byte)10, (byte)0, nullable, false, false, null];
    var baseline = TableDesignFingerprint.FromRows(Rows(Col(1, "Id", "int", false), Col(2, "Qty", "int")));
    Check(baseline == TableDesignFingerprint.FromRows(Rows(Col(1, "Id", "int", false), Col(2, "Qty", "int"))), "fingerprint-stable");
    Check(baseline != TableDesignFingerprint.FromRows(Rows(Col(1, "Id", "int", false), Col(2, "Qty", "bigint"))), "fingerprint-type-change");
    Check(baseline != TableDesignFingerprint.FromRows(Rows(Col(1, "Id", "int", false), Col(2, "Qty", "int", false))), "fingerprint-nullability-change");
    Check(baseline != TableDesignFingerprint.FromRows(Rows(Col(1, "Id", "int", false), Col(2, "Qty", "int"), Col(3, "New", "int"))), "fingerprint-added-column");
    Check(baseline != TableDesignFingerprint.FromRows(Rows(Col(1, "Id", "int", false, "2026-02-02T00:00:00.000"), Col(2, "Qty", "int", true, "2026-02-02T00:00:00.000"))), "fingerprint-modify-date");
    Check(TableDesignFingerprint.FromRows(Rows()) == TableDesignFingerprint.Absent && baseline != TableDesignFingerprint.Absent, "fingerprint-absent");
    var fpQuery = TableDesignFingerprint.BuildQuery(new TableRef("A]pp", "dbo", "Ord]ers"));
    Check(fpQuery.CommandText.Contains("[A]]pp].sys.objects", StringComparison.Ordinal) && !fpQuery.CommandText.Contains("Ord]ers", StringComparison.Ordinal)
          && fpQuery.Parameters.Count == 1 && (string)fpQuery.Parameters[0].Value! == "[A]]pp].[dbo].[Ord]]ers]", "fingerprint-query-quoted-and-parameterised");
    Pass("design-transactional-apply");
}

// WS5-T2: Edit Rows insert/delete (policy, SQL builders and atomic batches with fakes)
{
    var tbl = new TableRef("App", "dbo", "Orders");
    EditColumnInfo C(string n, string t, bool nullable = true, bool identity = false, bool computed = false) => new(n, t, nullable, identity, computed);

    // BuildInsert
    var ins = EditRowsSql.BuildInsert(tbl, new Dictionary<string, object?> { ["Qty"] = 5, ["Note"] = null, ["Na]me"] = "x" });
    Check(ins.CommandText.StartsWith("SET NOCOUNT ON;\nINSERT INTO [App].[dbo].[Orders] ([Qty], [Note], [Na]]me]) VALUES (@n0, @n1, @n2);", StringComparison.Ordinal), "insert-text:" + ins.CommandText);
    Check(ins.CommandText.Contains($";\nSELECT @@ROWCOUNT AS [{EditRowsSql.RowCountColumn}];", StringComparison.Ordinal), "insert-rowcount-immediate");
    Check(ins.Parameters.Count == 3 && ins.Parameters[0].Value is 5 && ins.Parameters[1].Value is null && !ins.CommandText.Contains("'x'", StringComparison.Ordinal), "insert-parameterised");
    var defaults = EditRowsSql.BuildInsert(tbl, new Dictionary<string, object?>());
    Check(defaults.CommandText.Contains("INSERT INTO [App].[dbo].[Orders] DEFAULT VALUES;", StringComparison.Ordinal) && defaults.Parameters.Count == 0, "insert-default-values");
    try { EditRowsSql.BuildInsert(tbl, new Dictionary<string, object?> { ["Body"] = new RawCellOmitted("too large", null, 1) }); throw new Exception("FAIL insert-omitted"); }
    catch (ArgumentException) { }
    try { EditRowsSql.BuildInsert(tbl, new Dictionary<string, object?> { [""] = 1 }); throw new Exception("FAIL insert-bad-name"); }
    catch (ArgumentException) { }

    // Policy: insert
    var support = EditRowsPolicy.Evaluate(["Id"],
        [C("Id", "int", false, identity: true), C("Qty", "int"), C("Total", "int", computed: true), C("Ver", "timestamp"), C("Name", "nvarchar")], false);
    var insertOk = EditRowsPolicy.EvaluateInsert(support,
        [C("Id", "int", false, identity: true), C("Qty", "int"), C("Total", "int", computed: true), C("Ver", "timestamp"), C("Name", "nvarchar")]);
    Check(insertOk.IsSupported && insertOk.WritableColumns.SequenceEqual(["Qty", "Name"]), "policy-insert-skips-identity-computed-rowversion:" + string.Join(",", insertOk.WritableColumns));
    var insertNoKey = EditRowsPolicy.EvaluateInsert(EditRowsPolicy.Evaluate([], [C("a", "int")], false), [C("a", "int")]);
    Check(!insertNoKey.IsSupported && !string.IsNullOrEmpty(insertNoKey.RefusalReason), "policy-insert-refused-without-key");
    var insertInstead = EditRowsPolicy.EvaluateInsert(
        EditRowsPolicy.Evaluate(["Id"], [C("Id", "int", false)], true), [C("Id", "int", false)]);
    Check(!insertInstead.IsSupported, "policy-insert-refused-instead-of-trigger");
    var unwritable = EditRowsPolicy.EvaluateInsert(
        EditRowsPolicy.Evaluate(["Id"], [C("Id", "int", false), C("At", "datetime2", false)], false),
        [C("Id", "int", false), C("At", "datetime2", false)]);
    Check(!unwritable.IsSupported && unwritable.RefusalReason!.Contains("At", StringComparison.Ordinal), "policy-insert-refuses-required-unwritable-column");
    var nullableUnwritable = EditRowsPolicy.EvaluateInsert(
        EditRowsPolicy.Evaluate(["Id"], [C("Id", "int", false), C("Doc", "xml")], false),
        [C("Id", "int", false), C("Doc", "xml")]);
    Check(!nullableUnwritable.IsSupported || !nullableUnwritable.WritableColumns.Contains("Doc"), "policy-insert-never-writes-uneditable-type");
    // Policy: delete
    Check(EditRowsPolicy.EvaluateDelete(support).IsSupported, "policy-delete-allowed-with-key");
    var deleteNoKey = EditRowsPolicy.EvaluateDelete(EditRowsPolicy.Evaluate([], [C("a", "int")], false));
    Check(!deleteNoKey.IsSupported && !string.IsNullOrEmpty(deleteNoKey.RefusalReason), "policy-delete-refused-without-key");
    Check(!EditRowsPolicy.EvaluateDelete(EditRowsPolicy.Evaluate(["Id"], [C("Id", "int")], true)).IsSupported, "policy-delete-refused-instead-of-trigger");

    var insert = EditRowsSql.BuildInsert(tbl, new Dictionary<string, object?> { ["Qty"] = 1 });
    var delete = EditRowsSql.BuildDelete(tbl, new RowKey(["Id"], [9]), new Dictionary<string, object?> { ["Qty"] = 4 });
    var update = EditRowsSql.BuildUpdate(tbl, new RowKey(["Id"], [2]), new Dictionary<string, object?> { ["Qty"] = 6 });

    // insert then conflicting update: whole set rolled back, nothing durable
    var insertThenConflict = new FakeEditTransactionFactory(1, 0);
    var r1 = await EditRowsBatch.ApplyAsync(insertThenConflict, [insert, update]);
    Check(r1.Outcome == EditBatchOutcome.RolledBackConflict && r1.FailedIndex == 1 && r1.WritesValidated == 1, "insert-then-conflict-outcome");
    Check(insertThenConflict.Log.Contains("rollback") && !insertThenConflict.Log.Contains("commit"), "insert-then-conflict-rolled-back");

    // delete conflict (row already gone / changed): zero rows rolls back
    var deleteConflict = new FakeEditTransactionFactory(0);
    var r2 = await EditRowsBatch.ApplyAsync(deleteConflict, [delete]);
    Check(r2.Outcome == EditBatchOutcome.RolledBackConflict && r2.FailedIndex == 0 && !deleteConflict.Log.Contains("commit"), "delete-conflict-rolls-back");

    // delete that touches more than one row (key not unique) rolls back
    var deleteMany = new FakeEditTransactionFactory(2);
    var r3 = await EditRowsBatch.ApplyAsync(deleteMany, [delete]);
    Check(r3.Outcome == EditBatchOutcome.RolledBackWrongRowCount && !deleteMany.Log.Contains("commit"), "delete-wrong-rowcount-rolls-back");

    // duplicate-key insert raises a provider error: earlier delete+insert are rolled back
    var dupKey = new FakeEditTransactionFactory(1, 1, new InvalidOperationException("Violation of PRIMARY KEY constraint"));
    var r4 = await EditRowsBatch.ApplyAsync(dupKey, [delete, update, insert]);
    Check(r4.Outcome == EditBatchOutcome.RolledBackError && r4.FailedIndex == 2 && dupKey.Log.Contains("rollback") && !dupKey.Log.Contains("commit"), "insert-duplicate-key-rolls-back-batch");

    // mixed delete/update/insert success commits once
    var good = new FakeEditTransactionFactory(1, 1, 1);
    var r5 = await EditRowsBatch.ApplyAsync(good, [delete, update, insert]);
    Check(r5.Committed && r5.WritesValidated == 3 && good.Log.Count(l => l == "commit") == 1, "mixed-insert-update-delete-commits");

    // the insert statement itself must report exactly one row
    var insertZero = new FakeEditTransactionFactory(0);
    Check((await EditRowsBatch.ApplyAsync(insertZero, [insert])).Outcome == EditBatchOutcome.RolledBackConflict && !insertZero.Log.Contains("commit"), "insert-zero-rows-rolls-back");

    // View code contains no raw SQL for the new operations (kept in Core)
    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SQLoryn.slnx")))
                return d.FullName;
        throw new Exception("FAIL repo root (SQLoryn.slnx) not found from test base directory");
    }
    var editWindow = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Sqloryn.App", "EditRowsWindow.axaml.cs"));
    var designWindow = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Sqloryn.App", "TableDesignWindow.axaml.cs"));
    Check(!editWindow.Contains("INSERT INTO", StringComparison.Ordinal) && !editWindow.Contains("DELETE FROM", StringComparison.Ordinal), "edit-window-no-raw-sql");
    Check(designWindow.Contains("ApplyTableDesignAsync", StringComparison.Ordinal) && !designWindow.Contains("_session.ExecuteAsync(current)", StringComparison.Ordinal), "design-window-uses-core-apply");
    Pass("edit-rows-insert-delete");
}

ExplorerCatalogTests.Run();
ConnectionTests.Run();

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
